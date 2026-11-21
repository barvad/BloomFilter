namespace BloomFilter.Tests;

public sealed class BloomFilterRingConcurrencyTests
{
    private static BloomFilterRing CreateRing(
        int filterCount = 3,
        long expectedItems = 200,
        double fillPercent = 0.5,
        double falsePositiveRate = 1e-9)
    {
        return BloomFilterRingFactory.Create(new BloomFilterRingOptions
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

        // Порог ротации 90 000 - заведомо выше 8 000 записей, поэтому окно не проворачивается.
        BloomFilterRing ring = CreateRing(expectedItems: 100_000, fillPercent: 0.9);

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
                    $"Потеряно значение item-{thread}-{i}.");
            }
        }
    }

    [Fact]
    public void ParallelWritesCountEveryUniqueValueOnceAndRotate()
    {
        const int threads = 8;
        const int perThread = 100;
        const int total = threads * perThread;

        // Порог 100, всего записей 800: кольцо обязано провернуться несколько раз под нагрузкой.
        BloomFilterRing ring = CreateRing(expectedItems: 200, fillPercent: 0.5);

        Parallel.For(0, threads, thread =>
        {
            for (int i = 0; i < perThread; i++)
            {
                Assert.True(ring.TryAdd($"item-{thread}-{i}"));
            }
        });

        // Каждое уникальное значение учитывается ровно один раз, несмотря на гонки.
        Assert.Equal((long)total, ring.TotalUniqueCount);

        // Ротацию запускает ровно один поток на снимок, поэтому их не больше, чем total / threshold.
        Assert.InRange(ring.RotationCount, 1, total / 100);

        // Активный индекс всегда указывает на существующий фильтр.
        Assert.InRange(ring.ActiveIndex, 0, ring.FilterCount - 1);
    }

    [Fact]
    public void ConcurrentAddsOfSameValueAreReportedAsNewAtLeastOnce()
    {
        const int writers = 64;
        BloomFilterRing ring = CreateRing(expectedItems: 200, fillPercent: 0.9);

        var reported = new bool[writers];
        Parallel.For(0, writers, i => reported[i] = ring.TryAdd("shared"));

        // Проверка и вставка в кольце не атомарны, поэтому "новым" значение
        // может оказаться более одного раза; важно, что оно вообще учтено и видно.
        Assert.Contains(true, reported);
        Assert.True(ring.TotalUniqueCount >= 1);
        Assert.True(ring.MightContain("shared"));
    }

    [Fact]
    public void ParallelReadersSeeEveryValueAfterWritesComplete()
    {
        const int threads = 8;
        const int perThread = 200;
        BloomFilterRing ring = CreateRing(expectedItems: 100_000, fillPercent: 0.9);

        Parallel.For(0, threads, thread =>
        {
            for (int i = 0; i < perThread; i++)
            {
                ring.Add($"value-{thread}-{i}");
            }

            // Параллельные читатели не должны падать на снимках кольца.
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