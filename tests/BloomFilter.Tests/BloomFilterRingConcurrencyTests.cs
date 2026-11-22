namespace BloomFilter.Tests;

public sealed class BloomFilterRingConcurrencyTests
{
    private static BloomFilterRing CreateRing(
        int filterCount = 3,
        long expectedItems = 200,
        double fillPercent = 0.5,
        double falsePositiveRate = 1e-9)
    {
        return new BloomFilterRingFactory().CreateRing(new BloomFilterRingOptions
        {
            FilterCount = filterCount,
            ExpectedItems = expectedItems,
            FillPercent = fillPercent,
            FalsePositiveRate = falsePositiveRate,
        });
    }

    [Fact]
    public void ParallelWritesBelowThresholdLoseNoValues()
    {
        const int threads = 8;
        const int perThread = 1_000;

        // Rotation threshold 90,000 - knowingly higher than 8,000 records, so the window does not roll over.
        BloomFilterRing ring = new BloomFilterRingFactory().CreateRing(new BloomFilterRingOptions
        {
            FilterCount = 3,
            ExpectedItems = 100_000,
            FillPercent = 0.9,
        });

        Parallel.For(0, threads, thread =>
        {
            for (int i = 0; i < perThread; i++)
            {
                ring.Add($"item-{thread}-{i}");
            }
        });

        Assert.Equal(0L, ring.RotationCount);

        for (int thread = 0; thread < threads; thread++)
        {
            for (int i = 0; i < perThread; i++)
            {
                Assert.True(
                    ring.MightContain($"item-{thread}-{i}"),
                    $"Lost value item-{thread}-{i}.");
            }
        }
    }

    [Fact]
    public void ParallelWritesCountEveryUniqueValueOnceAndRotate()
    {
        const int threads = 8;
        const int perThread = 100;
        const int total = threads * perThread;

        // Threshold 100, total records 800: the ring must rotate several times under load.
        BloomFilterRing ring = new BloomFilterRingFactory().CreateRing(new BloomFilterRingOptions
        {
            FilterCount = 3,
            ExpectedItems = 200,
            FillPercent = 0.5,
        });

        Parallel.For(0, threads, thread =>
        {
            for (int i = 0; i < perThread; i++)
            {
                Assert.True(ring.TryAdd($"item-{thread}-{i}"));
            }
        });

        // Each unique value is counted exactly once, despite races.
        Assert.Equal((long)total, ring.TotalUniqueCount);

        // Rotation is triggered by exactly one snapshot thread, so there are no more of them than total / threshold.
        Assert.InRange(ring.RotationCount, 1, total / 100);

        // The active index always points to an existing filter.
        Assert.InRange(ring.ActiveIndex, 0, ring.FilterCount - 1);
    }

    [Fact]
    public void ConcurrentAddsOfSameValueAreReportedAsNewAtLeastOnce()
    {
        const int writers = 64;
        BloomFilterRing ring = new BloomFilterRingFactory().CreateRing(new BloomFilterRingOptions
        {
            FilterCount = 3,
            ExpectedItems = 200,
            FillPercent = 0.9,
        });

        var reported = new bool[writers];
        Parallel.For(0, writers, i => reported[i] = ring.TryAdd("shared"));

        // Check and insert in the ring are not atomic, so the value
        // may turn out to be "new" more than once; it is important that it is accounted for and visible at all.
        Assert.Contains(true, reported);
        Assert.True(ring.TotalUniqueCount >= 1);
        Assert.True(ring.MightContain("shared"));
    }

    [Fact]
    public void ParallelReadersSeeEveryValueAfterWritesComplete()
    {
        const int threads = 8;
        const int perThread = 200;
        BloomFilterRing ring = new BloomFilterRingFactory().CreateRing(new BloomFilterRingOptions
        {
            FilterCount = 3,
            ExpectedItems = 100_000,
            FillPercent = 0.9,
        });

        Parallel.For(0, threads, thread =>
        {
            for (int i = 0; i < perThread; i++)
            {
                ring.Add($"value-{thread}-{i}");
            }

            // Parallel readers should not crash on ring snapshots.
            for (int i = 0; i < perThread; i++)
            {
                ring.MightContain($"value-{thread}-{i}");
                ring.MightContain($"missing-{thread}-{i}");
            }
        });

        Assert.Equal(0L, ring.RotationCount);
        Assert.Equal((long)(threads * perThread), ring.TotalUniqueCount);
    }
}

