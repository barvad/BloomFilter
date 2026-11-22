using System.Runtime.InteropServices;

namespace BloomFilter.Tests;

public sealed class BloomFilterRingTests
{
    // Capacity 200 with 0.5 fill gives a rotation threshold of exactly 100 values.
    private const int GenerationSize = 100;

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

    private static string[] Generation(string prefix, int count = GenerationSize)
    {
        return Enumerable.Range(0, count).Select(i => $"{prefix}-{i}").ToArray();
    }

    private static void AddAll(BloomFilterRing ring, IEnumerable<string> items)
    {
        foreach (string item in items)
        {
            ring.Add(item);
        }
    }

    [Fact]
    public void NewRingIsEmpty()
    {
        BloomFilterRing ring = CreateRing();

        Assert.Equal(3, ring.FilterCount);
        Assert.Equal(100L, ring.RotationThreshold);
        Assert.Equal(0, ring.ActiveIndex);
        Assert.Equal(0L, ring.ActiveUniqueCount);
        Assert.Equal(0L, ring.TotalUniqueCount);
        Assert.Equal(0L, ring.RotationCount);
        Assert.False(ring.MightContain("anything"));
    }

    [Fact]
    public void TryAddReturnsTrueOnlyForFirstOccurrence()
    {
        BloomFilterRing ring = CreateRing();

        Assert.True(ring.TryAdd("value"));
        Assert.False(ring.TryAdd("value"));

        Assert.Equal(1L, ring.ActiveUniqueCount);
        Assert.Equal(1L, ring.TotalUniqueCount);
        Assert.Equal(0L, ring.RotationCount);
    }

    [Fact]
    public void StringAndUtf16BytesOverloadsAddressTheSameValue()
    {
        BloomFilterRing ring = CreateRing();
        byte[] bytes = MemoryMarshal.AsBytes("value".AsSpan()).ToArray();

        Assert.True(ring.TryAdd("value"));

        Assert.True(ring.MightContain(bytes));
        Assert.False(ring.TryAdd(bytes));
    }

    [Fact]
    public void AddOverloadsMakeValueVisible()
    {
        BloomFilterRing ring = CreateRing();
        byte[] bytes = [9, 8, 7, 6];

        ring.Add("text");
        ring.Add(bytes);

        Assert.True(ring.MightContain("text"));
        Assert.True(ring.MightContain(bytes));
    }

    [Fact]
    public void RotationHappensExactlyAtThreshold()
    {
        BloomFilterRing ring = CreateRing();

        for (int i = 0; i < GenerationSize - 1; i++)
        {
            Assert.True(ring.TryAdd($"gen0-{i}"));
        }

        Assert.Equal(0, ring.ActiveIndex);
        Assert.Equal(0L, ring.RotationCount);
        Assert.Equal(99L, ring.ActiveUniqueCount);

        // The hundredth unique value fills the active filter and rotates the ring.
        Assert.True(ring.TryAdd($"gen0-{GenerationSize - 1}"));

        Assert.Equal(1, ring.ActiveIndex);
        Assert.Equal(1L, ring.RotationCount);
        Assert.Equal(0L, ring.ActiveUniqueCount);
        Assert.Equal(100L, ring.TotalUniqueCount);
    }

    [Fact]
    public void ActiveIndexCyclesThroughEveryFilter()
    {
        BloomFilterRing ring = CreateRing(filterCount: 3);

        Assert.Equal(0, ring.ActiveIndex);

        AddAll(ring, Generation("gen0"));
        Assert.Equal(1, ring.ActiveIndex);

        AddAll(ring, Generation("gen1"));
        Assert.Equal(2, ring.ActiveIndex);

        AddAll(ring, Generation("gen2"));
        Assert.Equal(0, ring.ActiveIndex);

        Assert.Equal(3L, ring.RotationCount);
        Assert.Equal(300L, ring.TotalUniqueCount);
        Assert.Equal(0L, ring.ActiveUniqueCount);
    }

    [Fact]
    public void ValuesStayVisibleUntilTheirFilterIsReused()
    {
        BloomFilterRing ring = CreateRing(filterCount: 3);
        string[] gen0 = Generation("gen0");
        string[] gen1 = Generation("gen1");
        string[] gen2 = Generation("gen2");

        AddAll(ring, gen0);
        AddAll(ring, gen1);

        // After two rotations, both generations are still in the ring.
        Assert.Equal(2L, ring.RotationCount);
        Assert.All(gen0, item => Assert.True(ring.MightContain(item)));
        Assert.All(gen1, item => Assert.True(ring.MightContain(item)));

        // The third rotation reuses the filter of generation gen0 - it leaves the window.
        AddAll(ring, gen2);

        Assert.Equal(3L, ring.RotationCount);
        Assert.All(gen0, item => Assert.False(ring.MightContain(item)));
        Assert.All(gen1, item => Assert.True(ring.MightContain(item)));
        Assert.All(gen2, item => Assert.True(ring.MightContain(item)));
    }

    [Fact]
    public void ValueFromPreviousGenerationIsNotAddedAgain()
    {
        BloomFilterRing ring = CreateRing();

        Assert.True(ring.TryAdd("old"));

        // Completes the generation up to the threshold together with the old value: 1 + 99 = 100.
        for (int i = 0; i < GenerationSize - 1; i++)
        {
            ring.Add($"gen0-{i}");
        }

        Assert.Equal(1L, ring.RotationCount);

        // The old value is still in the window, so it is not added again.
        Assert.False(ring.TryAdd("old"));
        Assert.Equal(100L, ring.TotalUniqueCount);
    }

    [Fact]
    public void CounterOfActiveFilterStartsFromZeroAfterRotation()
    {
        BloomFilterRing ring = CreateRing();
        AddAll(ring, Generation("gen0"));

        Assert.Equal(0L, ring.ActiveUniqueCount);
        Assert.Equal(100L, ring.TotalUniqueCount);

        ring.Add("fresh");

        Assert.Equal(1L, ring.ActiveUniqueCount);
        Assert.Equal(101L, ring.TotalUniqueCount);
        Assert.Equal(1, ring.ActiveIndex);
    }

    [Fact]
    public void DuplicatesDoNotAdvanceCounters()
    {
        BloomFilterRing ring = CreateRing();

        for (int i = 0; i < 10; i++)
        {
            ring.Add("same");
        }

        Assert.Equal(1L, ring.ActiveUniqueCount);
        Assert.Equal(1L, ring.TotalUniqueCount);
        Assert.Equal(0L, ring.RotationCount);
    }

    [Fact]
    public void ClearRestoresInitialStateAndForgetsValues()
    {
        BloomFilterRing ring = CreateRing();
        string[] values = [.. Generation("gen0"), .. Generation("gen1")];
        AddAll(ring, values);

        Assert.Equal(2L, ring.RotationCount);

        ring.Clear();

        Assert.Equal(0, ring.ActiveIndex);
        Assert.Equal(0L, ring.ActiveUniqueCount);
        Assert.Equal(0L, ring.TotalUniqueCount);
        Assert.Equal(0L, ring.RotationCount);
        Assert.All(values, item => Assert.False(ring.MightContain(item)));

        // After clearing, the ring is again ready for use.
        Assert.True(ring.TryAdd("fresh"));
        Assert.True(ring.MightContain("fresh"));
        Assert.Equal(1L, ring.TotalUniqueCount);
    }

    [Fact]
    public void RingWithThresholdOfOneRotatesOnEveryInsert()
    {
        BloomFilterRing ring = BloomFilterRingFactory.Create(
            filterCount: 2,
            expectedItems: 1,
            falsePositiveRate: 0.01,
            fillPercent: 0.001);

        Assert.Equal(1L, ring.RotationThreshold);

        Assert.True(ring.TryAdd("a"));
        Assert.Equal(1L, ring.RotationCount);
        Assert.Equal(1, ring.ActiveIndex);
        Assert.True(ring.MightContain("a"));

        Assert.True(ring.TryAdd("b"));
        Assert.Equal(2L, ring.RotationCount);
        Assert.Equal(0, ring.ActiveIndex);
        Assert.Equal(2L, ring.TotalUniqueCount);
    }

    [Fact]
    public void CountingReachesThresholdForEachGeneration()
    {
        BloomFilterRing ring = CreateRing(filterCount: 4, expectedItems: 200, fillPercent: 0.25);
        Assert.Equal(50L, ring.RotationThreshold);

        for (int generation = 1; generation <= 8; generation++)
        {
            AddAll(ring, Generation($"gen{generation}", count: 50));

            Assert.Equal(generation, ring.RotationCount);
            Assert.Equal(generation * 50L, ring.TotalUniqueCount);
            Assert.Equal(generation % 4, ring.ActiveIndex);
        }
    }
}
