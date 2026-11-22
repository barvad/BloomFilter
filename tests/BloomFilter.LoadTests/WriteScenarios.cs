using System.Diagnostics;

namespace BloomFilter.LoadTests;

internal static class WriteScenarios
{
    private const int BatchSize = 1024;

    /// <summary>
    /// Single-threaded writing to the base filter and to the ring without rotation.
    /// </summary>
    public static ScenarioReport RunSingleThreaded(ProfileSettings settings)
    {
        long items = settings.WriteItems;
        var report = new ScenarioReport($"Single-threaded writing: {Fmt.N(items)} unique values");

        Span<char> buffer = stackalloc char[32];

        // Warm-up is performed on separate instances to avoid polluting the measured ring.
        WarmUp(buffer, 10_000);

        var filter = new ConcurrentBloomFilter(items, 0.001);
        BloomFilterRing ring = BloomFilterRingFactory.Create(3, items + 1, 0.001, fillPercent: 1.0);

        var filterLatency = new LatencySamples();
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var filterWatch = Stopwatch.StartNew();

        long batchStart = Stopwatch.GetTimestamp();
        for (long i = 0; i < items; i++)
        {
            int length = KeyWriter.Write(buffer, "single-", i);
            filter.Add(buffer[..length]);

            if ((i & (BatchSize - 1)) == BatchSize - 1)
            {
                filterLatency.Add(BatchTimer.TicksToMicrosecondsValue * (Stopwatch.GetTimestamp() - batchStart) / BatchSize);
                batchStart = Stopwatch.GetTimestamp();
            }
        }

        filterWatch.Stop();
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();

        var ringLatency = new LatencySamples();
        var ringWatch = Stopwatch.StartNew();

        batchStart = Stopwatch.GetTimestamp();
        for (long i = 0; i < items; i++)
        {
            int length = KeyWriter.Write(buffer, "ring-", i);
            ring.Add(buffer[..length]);

            if ((i & (BatchSize - 1)) == BatchSize - 1)
            {
                ringLatency.Add(BatchTimer.TicksToMicrosecondsValue * (Stopwatch.GetTimestamp() - batchStart) / BatchSize);
                batchStart = Stopwatch.GetTimestamp();
            }
        }

        ringWatch.Stop();

        double filterRate = items / filterWatch.Elapsed.TotalSeconds;
        double ringRate = items / ringWatch.Elapsed.TotalSeconds;
        long allocated = allocAfter - allocBefore;

        report.Row("Add, base filter", Fmt.Rate(filterRate));
        report.Row("Add, ring (no rotation)", Fmt.Rate(ringRate));
        report.Row("Add latency, base filter", filterLatency.Summary());
        report.Row("Add latency, ring", ringLatency.Summary());
        report.Row("Heap allocations", $"{Fmt.N(allocated)} B ({allocated / (double)items:N4} B/op)");
        report.Row("Unique values counted, ring", $"{Fmt.N(ring.TotalUniqueCount)} of {Fmt.N(items)}");
        report.Row("Rotations", Fmt.N(ring.RotationCount));

        report.Check("No rotations expected", ring.RotationCount == 0, $"got {ring.RotationCount}");
        report.Check(
            "At least 99% of unique values counted",
            ring.TotalUniqueCount >= (long)(items * 0.99),
            $"counted {ring.TotalUniqueCount}");
        report.Check(
            "Writing does not allocate on the heap",
            allocated < 64 * 1024,
            $"allocated {allocated} B");

        // Bits only accumulate, so every submitted value must be present.
        long sampled = 0;
        long present = 0;
        for (long i = 0; i < items; i += Math.Max(1, items / 10_000))
        {
            int length = KeyWriter.Write(buffer, "single-", i);
            sampled++;
            if (filter.MightContain(buffer[..length]))
            {
                present++;
            }
        }

        report.Check("No false negatives in the base filter", present == sampled, $"{present} of {sampled}");
        report.Throughput = ringRate;
        return report;
    }

    /// <summary>
    /// Parallel writing to a ring whose size deliberately prevents rotation:
    /// verify that no submitted value is lost under load.
    /// </summary>
    public static ScenarioReport RunParallel(ProfileSettings settings)
    {
        long total = settings.ParallelItems;
        int threads = settings.Threads;

        // The threshold is one greater than the total number of writes, so the ring does not rotate.
        BloomFilterRing ring = BloomFilterRingFactory.Create(3, total + 1, 0.001, fillPercent: 1.0);
        var report = new ScenarioReport(
            $"Parallel writing without rotation: {Fmt.N(total)} values, {threads} threads");

        long baseBudget = total / threads;

        var wall = Stopwatch.StartNew();
        WorkerResult[] results = ParallelRunner.Run(threads, (thread, _) =>
        {
            long start = thread * baseBudget;
            long end = thread == threads - 1 ? total : start + baseBudget;
            long added = 0;
            long failures = 0;
            var latency = new LatencySamples();
            Span<char> buffer = stackalloc char[32];
            var workerWatch = Stopwatch.StartNew();

            long batchStart = Stopwatch.GetTimestamp();
            long inBatch = 0;
            for (long i = start; i < end; i++)
            {
                int length = KeyWriter.Write(buffer, "par-", i);

                if (ring.TryAdd(buffer[..length]))
                {
                    added++;
                }
                else
                {
                    failures++;
                }

                if (++inBatch == BatchSize)
                {
                    latency.Add(BatchTimer.TicksToMicrosecondsValue * (Stopwatch.GetTimestamp() - batchStart) / BatchSize);
                    batchStart = Stopwatch.GetTimestamp();
                    inBatch = 0;
                }
            }

            workerWatch.Stop();
            return new WorkerResult(added, failures, workerWatch.Elapsed, latency);
        });

        wall.Stop();

        long successes = ParallelRunner.SumOperations(results);
        long rejected = ParallelRunner.SumFailures(results);
        double rate = successes / wall.Elapsed.TotalSeconds;

        report.Row("Unique values accepted", Fmt.N(successes));
        report.Row("Rejected as already known", Fmt.N(rejected));
        report.Row("Elapsed time", Fmt.Duration(wall.Elapsed));
        report.Row("Throughput", Fmt.Rate(rate));
        report.Row("Write latency", ParallelRunner.MergeLatency(results).Summary());
        report.Row("Total counted by ring", Fmt.N(ring.TotalUniqueCount));
        report.Row("Rotations", Fmt.N(ring.RotationCount));

        report.Check("No rotations expected", ring.RotationCount == 0, $"got {ring.RotationCount}");
        report.Check(
            "Ring counter matches accepted values",
            ring.TotalUniqueCount == successes,
            $"ring {ring.TotalUniqueCount}, accepted {successes}");
        report.Check(
            "Rejections do not exceed 1% (false positives)",
            rejected <= total / 100,
            $"rejected {rejected}");

        // In a non-rotating ring, all submitted keys must be reported as present.
        long missing = 0;
        long sampled = 0;
        Span<char> check = stackalloc char[32];
        for (long i = 0; i < total; i += Math.Max(1, total / 20_000))
        {
            int length = KeyWriter.Write(check, "par-", i);
            sampled++;
            if (!ring.MightContain(check[..length]))
            {
                missing++;
            }
        }

        report.Check("No false negatives", missing == 0, $"missing {missing} of {sampled}");
        report.Throughput = rate;
        return report;
    }

    private static void WarmUp(Span<char> buffer, int count)
    {
        // One-time instances: only JIT and code-path warm-up matter.
        var filter = new ConcurrentBloomFilter(count * 2, 0.001);
        BloomFilterRing ring = BloomFilterRingFactory.Create(3, count * 2, 0.001, fillPercent: 1.0);

        for (int i = 0; i < count; i++)
        {
            int length = KeyWriter.Write(buffer, "warmup-", i);
            filter.Add(buffer[..length]);
            ring.Add(buffer[..length]);
        }
    }
}

