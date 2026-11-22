using System.Diagnostics;

namespace BloomFilter.LoadTests;

internal static class ReadScenarios
{
    private const int BatchSize = 4096;

    /// <summary>
    /// Parallel reading of the ring filled to the rotation threshold.
    /// </summary>
    public static ScenarioReport RunRead(ProfileSettings settings)
    {
        long total = settings.ReadItems;
        int threads = settings.Threads;
        BloomFilterRing ring = BloomFilterRingFactory.Create(3, total + 1, 0.001, fillPercent: 1.0);
        var report = new ScenarioReport($"Parallel reading: {Fmt.N(total)} keys, {threads} threads");

        long perThread = total / threads;

        var fillWatch = Stopwatch.StartNew();
        ParallelRunner.Run(threads, (thread, _) =>
        {
            long start = thread * perThread;
            long end = thread == threads - 1 ? total : start + perThread;
            Span<char> buffer = stackalloc char[32];

            for (long i = start; i < end; i++)
            {
                int length = KeyWriter.Write(buffer, "read-", i);
                ring.Add(buffer[..length]);
            }

            return new WorkerResult(end - start, 0, TimeSpan.Zero, new LatencySamples());
        });
        fillWatch.Stop();

        TimeSpan budget = settings.RotationDuration;
        var readWatch = Stopwatch.StartNew();
        WorkerResult[] results = ParallelRunner.Run(threads, (thread, context) =>
            {
                long start = thread * perThread;
                long end = thread == threads - 1 ? total : start + perThread;
                long reads = 0;
                long missing = 0;
                var latency = new LatencySamples();
                Span<char> buffer = stackalloc char[32];
                var workerWatch = Stopwatch.StartNew();

                long batchStart = Stopwatch.GetTimestamp();
                long inBatch = 0;

                do
                {
                    for (long i = start; i < end; i++)
                    {
                        int length = KeyWriter.Write(buffer, "read-", i);
                        if (!ring.MightContain(buffer[..length]))
                        {
                            missing++;
                        }

                        reads++;

                        if (++inBatch == BatchSize)
                        {
                            latency.Add(BatchTimer.TicksToMicrosecondsValue * (Stopwatch.GetTimestamp() - batchStart) / BatchSize);
                            batchStart = Stopwatch.GetTimestamp();
                            inBatch = 0;
                        }
                    }
                }
                while (!context.Expired(budget));

                workerWatch.Stop();
                return new WorkerResult(reads, missing, workerWatch.Elapsed, latency);
            });
        readWatch.Stop();

        long readsTotal = ParallelRunner.SumOperations(results);
        long missingTotal = ParallelRunner.SumFailures(results);
        double readRate = readsTotal / readWatch.Elapsed.TotalSeconds;

        report.Row("Filling", $"{Fmt.Rate(total / fillWatch.Elapsed.TotalSeconds)}, {Fmt.Duration(fillWatch.Elapsed)}");
        report.Row("Read", Fmt.N(readsTotal));
        report.Row("Reading throughput", Fmt.Rate(readRate));
        report.Row("Reading latency", ParallelRunner.MergeLatency(results).Summary());
        report.Row("False negatives", Fmt.N(missingTotal));
        report.Row("Rotations", Fmt.N(ring.RotationCount));

        report.Check("Unexpected rotations", ring.RotationCount == 0, $"received {ring.RotationCount}");
        report.Check("No false negatives on read", missingTotal == 0, $"skipped {missingTotal}");
        report.Check(
            "At least 99% of keys are filled",
            ring.TotalUniqueCount >= (long)(total * 0.99),
            $"considered {ring.TotalUniqueCount}");

        report.Throughput = readRate;
        return report;
    }

    /// <summary>
    /// Scrolling the ring under load with memory window verification.
    /// Last generations should be readable, and generations whose filters are reused should not.
    /// </summary>
    /// <remarks>
    /// The ring is deliberately small, so false positives may occasionally occur
    /// (especially with very low FPR and small bitmap size). They do not break
    /// invariants: counters are calculated based on actually accepted values, and the window border
    /// is checked with a margin of one generation.
    /// </remarks>
    public static ScenarioReport RunRotation(ProfileSettings settings)
    {
        const long expectedItems = 10_000;
        const double falsePositiveRate = 1e-9;
        const int filterCount = 3;
        const int sampleSize = 64;
        const int sampleSlots = 5;

        BloomFilterRing ring = BloomFilterRingFactory.Create(
            filterCount,
            expectedItems,
            falsePositiveRate,
            fillPercent: 0.9);

        long threshold = ring.RotationThreshold;
        int generations = settings.RotationGenerations;
        long totalOps = generations * threshold;

        var report = new ScenarioReport(
            $"Rotation under load: {Fmt.N(generations)} generations of {Fmt.N(threshold)} values");

        var samples = new string[sampleSlots][];
        var latency = new LatencySamples();
        long accepted = 0;
        long rejected = 0;
        long rotationFailures = 0;
        long windowFailures = 0;
        long totalAddTicks = 0;

        Span<char> buffer = stackalloc char[48];

        for (int generation = 1; generation <= generations; generation++)
        {
            int slot = (generation - 1) % sampleSlots;
            samples[slot] = BuildSamples(generation, threshold, sampleSize);

            string prefix = $"rot-{generation}-";
            long generationStart = Stopwatch.GetTimestamp();

            for (long i = 0; i < threshold; i++)
            {
                int length = KeyWriter.Write(buffer, prefix, i);
                if (ring.TryAdd(buffer[..length]))
                {
                    accepted++;
                }
                else
                {
                    rejected++;
                }
            }

            long generationTicks = Stopwatch.GetTimestamp() - generationStart;
            totalAddTicks += generationTicks;
            latency.Add(BatchTimer.TicksToMicrosecondsValue * generationTicks / threshold);

            // Rotation occurs exactly at each threshold of accepted values.
            long expectedRotations = accepted / threshold;

            if (ring.RotationCount != expectedRotations || ring.TotalUniqueCount != accepted)
            {
                rotationFailures++;
            }

            if (ring.ActiveIndex != (int)(expectedRotations % filterCount))
            {
                rotationFailures++;
            }

            // The current and previous generations must be in the ring.
            if (!AllPresent(ring, samples[slot], expected: true))
            {
                windowFailures++;
            }

            if (generation >= 2 && !AllPresent(ring, samples[(generation - 2) % sampleSlots], expected: true))
            {
                windowFailures++;
            }

            // Generation g-2 is reused in this rotation: rare false positives are allowed
            // after cleaning, but no more than 5% of the sample.
            if (generation >= 3 && PresentCount(ring, samples[(generation - 3) % sampleSlots]) > sampleSize / 20)
            {
                windowFailures++;
            }

            // Generation g-3 has left the window guaranteed, even if the boundary has shifted.
            if (generation >= 4 && !AllPresent(ring, samples[(generation - 4) % sampleSlots], expected: false))
            {
                windowFailures++;
            }
        }

        double totalAddSeconds = totalAddTicks / (double)Stopwatch.Frequency;
        double rate = totalOps / totalAddSeconds;

        report.Row("Addition operations", Fmt.N(totalOps));
        report.Row("Accepted uniques", Fmt.N(accepted));
        report.Row("Rejected by false positives", Fmt.N(rejected));
        report.Row("Addition time", Fmt.Duration(TimeSpan.FromSeconds(totalAddSeconds)));
        report.Row("Throughput", Fmt.Rate(rate));
        report.Row("Addition latency", latency.Summary());
        report.Row("Rotations", Fmt.N(ring.RotationCount));
        report.Row("Active index", ring.ActiveIndex.ToString());
        report.Row("Recorded uniques", Fmt.N(ring.TotalUniqueCount));

        report.Check(
            "Rejection rate no more than 0.1%",
            rejected <= totalOps / 1_000,
            $"rejected {rejected} of {totalOps}");
        report.Check(
            "Rotation and unique counters match accepted values",
            rotationFailures == 0,
            $"discrepancies {rotationFailures}");
        report.Check(
            "Memory window matches the three-filter ring",
            windowFailures == 0,
            $"violations {windowFailures}");

        report.Throughput = rate;
        return report;
    }

    private static string[] BuildSamples(int generation, long threshold, int sampleSize)
    {
        var result = new string[sampleSize];
        long step = Math.Max(1, threshold / sampleSize);

        for (int i = 0; i < sampleSize; i++)
        {
            long value = (i * step) % threshold;
            result[i] = $"rot-{generation}-{value}";
        }

        return result;
    }

    private static bool AllPresent(BloomFilterRing ring, string[] values, bool expected)
    {
        foreach (string value in values)
        {
            if (ring.MightContain(value) != expected)
            {
                return false;
            }
        }

        return true;
    }

    private static int PresentCount(BloomFilterRing ring, string[] values)
    {
        int present = 0;

        foreach (string value in values)
        {
            if (ring.MightContain(value))
            {
                present++;
            }
        }

        return present;
    }
}
