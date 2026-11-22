using System.Diagnostics;

namespace BloomFilter.LoadTests;

internal static class CapacityScenarios
{
    private const double TargetRate = 0.001;

    /// <summary>
    /// The actual false positive rate of the filter when filled to the calculated capacity.
    /// </summary>
    public static ScenarioReport RunFalsePositiveRate(ProfileSettings settings)
    {
        int items = settings.FprItems;
        int probes = settings.FprProbes;
        var report = new ScenarioReport(
            $"False positives: {Fmt.N(items)} items, {Fmt.N(probes)} checks of absent keys");

        Span<char> buffer = stackalloc char[32];

        var filter = new ConcurrentBloomFilter(items, TargetRate);
        var fillWatch = Stopwatch.StartNew();
        for (int i = 0; i < items; i++)
        {
            int length = KeyWriter.Write(buffer, "known-", i);
            filter.Add(buffer[..length]);
        }

        fillWatch.Stop();

        long filterFalsePositives = CountFalsePositives(filter, probes);
        double filterRate = filterFalsePositives / (double)probes;

        // The same for the ring: fill with one item less than the threshold to avoid rotating.
        BloomFilterRing ring = BloomFilterRingFactory.Create(3, items, TargetRate, fillPercent: 1.0);
        for (int i = 0; i < items - 1; i++)
        {
            int length = KeyWriter.Write(buffer, "ring-known-", i);
            ring.Add(buffer[..length]);
        }

        long ringFalsePositives = CountFalsePositives(ring, probes);
        double ringRate = ringFalsePositives / (double)probes;

        report.Row("Filter capacity", Fmt.N(items));
        report.Row("Base filter size", Fmt.Bytes(filter.SizeInBytes));
        report.Row("Target false positive rate", $"{TargetRate:P3}");
        report.Row("Actual, base filter", $"{filterRate:P4} ({Fmt.N(filterFalsePositives)})");
        report.Row("Actual, ring", $"{ringRate:P4} ({Fmt.N(ringFalsePositives)})");
        report.Row("Fill time", Fmt.Duration(fillWatch.Elapsed));

        double limit = TargetRate * 2.5 + 0.0005;
        report.Check("Base filter false positive rate within tolerance", filterRate <= limit, $"{filterRate:P4} > {limit:P4}");
        report.Check("Ring false positive rate within tolerance", ringRate <= limit, $"{ringRate:P4} > {limit:P4}");
        report.Check("Ring did not rotate", ring.RotationCount == 0, $"rotations {ring.RotationCount}");
        return report;
    }

    /// <summary>
    /// Memory usage of the ring and absence of allocations during operations.
    /// </summary>
    public static ScenarioReport RunMemory(ProfileSettings settings)
    {
        long expected = settings.WriteItems;
        var report = new ScenarioReport($"Memory: ring for {Fmt.N(expected)} items in filter");

        long before = GC.GetTotalMemory(forceFullCollection: true);
        BloomFilterRing ring = BloomFilterRingFactory.Create(3, expected, 0.001, fillPercent: 1.0);
        long after = GC.GetTotalMemory(forceFullCollection: true);
        long measured = after - before;

        double bitsPerFilter = -(expected * Math.Log(0.001)) / (Math.Log(2) * Math.Log(2));
        long perFilterBytes = (long)Math.Ceiling(bitsPerFilter / 64.0) * sizeof(ulong);
        long theoretical = perFilterBytes * ring.FilterCount;

        report.Row("Managed heap before", Fmt.Bytes(before));
        report.Row("Managed heap after", Fmt.Bytes(after));
        report.Row("Measured ring size", Fmt.Bytes(measured));
        report.Row("Theoretical ring size", Fmt.Bytes(theoretical));
        report.Row("Bytes per item", $"{(measured / (double)(expected * ring.FilterCount)):N2}");

        report.Check(
            "Measured size close to theoretical",
            measured >= theoretical / 2 && measured <= theoretical * 2 + 32L * 1024 * 1024,
            $"measured {measured} B, expected around {theoretical} B");

        long operations = Math.Min(settings.ParallelItems, 2_000_000);
        long capacity = Math.Max(expected, operations);
        var filter = new ConcurrentBloomFilter(capacity, 0.001);
        Span<char> buffer = stackalloc char[32];

        for (int i = 0; i < 20_000; i++)
        {
            int length = KeyWriter.Write(buffer, "warm-", i);
            filter.TryAdd(buffer[..length]);
        }

        int gen0 = GC.CollectionCount(0);
        int gen1 = GC.CollectionCount(1);
        int gen2 = GC.CollectionCount(2);
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();

        for (long i = 0; i < operations; i++)
        {
            int length = KeyWriter.Write(buffer, "mem-", i);
            filter.TryAdd(buffer[..length]);
        }

        for (long i = 0; i < operations; i++)
        {
            int length = KeyWriter.Write(buffer, "mem-", i);
            filter.MightContain(buffer[..length]);
        }

        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

        report.Row("Read and write operations", Fmt.N(operations * 2));
        report.Row("Operations time", Fmt.Duration(watch.Elapsed));
        report.Row("Heap allocations during operations", $"{Fmt.N(allocated)} B ({allocated / (double)(operations * 2):N4} B/op)");
        report.Row(
            "Garbage collections (0/1/2)",
            $"{GC.CollectionCount(0) - gen0}/{GC.CollectionCount(1) - gen1}/{GC.CollectionCount(2) - gen2}");

        report.Check(
            "Filter operations do not allocate memory on the heap",
            allocated < 64 * 1024,
            $"allocated {Fmt.N(allocated)} B");
        return report;
    }

    private static long CountFalsePositives(ConcurrentBloomFilter filter, int probes)
    {
        Span<char> buffer = stackalloc char[32];
        long falsePositives = 0;

        for (int i = 0; i < probes; i++)
        {
            int length = KeyWriter.Write(buffer, "probe-", i);
            if (filter.MightContain(buffer[..length]))
            {
                falsePositives++;
            }
        }

        return falsePositives;
    }

    private static long CountFalsePositives(BloomFilterRing ring, int probes)
    {
        Span<char> buffer = stackalloc char[32];
        long falsePositives = 0;

        for (int i = 0; i < probes; i++)
        {
            int length = KeyWriter.Write(buffer, "ring-probe-", i);
            if (ring.MightContain(buffer[..length]))
            {
                falsePositives++;
            }
        }

        return falsePositives;
    }
}
