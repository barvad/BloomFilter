using System.Runtime.InteropServices;

namespace BloomFilter.Tests;

public sealed class ConcurrentBloomFilterTests
{
    private static byte[] Utf16Bytes(string value) => MemoryMarshal.AsBytes(value.AsSpan()).ToArray();

    private static int WriteKey(Span<char> destination, ReadOnlySpan<char> prefix, long value)
    {
        prefix.CopyTo(destination);
        value.TryFormat(destination[prefix.Length..], out int written);
        return prefix.Length + written;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1_000)]
    public void ConstructorRejectsNonPositiveExpectedItems(long expectedItems)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConcurrentBloomFilter(expectedItems));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void ConstructorRejectsFalsePositiveRateOutsideOpenUnitInterval(double falsePositiveRate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ConcurrentBloomFilter(1_000, falsePositiveRate));
    }

    [Fact]
    public void SizeInBytesIsPositiveAndAlignedToUlong()
    {
        var filter = new ConcurrentBloomFilter(1_000, 0.01);

        Assert.True(filter.SizeInBytes > 0);
        Assert.Equal(0, filter.SizeInBytes % sizeof(ulong));
    }

    [Fact]
    public void EmptyFilterContainsNothing()
    {
        var filter = new ConcurrentBloomFilter(1_000, 0.001);

        Assert.False(filter.MightContain("absent"));
        Assert.False(filter.MightContain(Utf16Bytes("absent")));
    }

    [Fact]
    public void AddedStringIsReportedAsContained()
    {
        var filter = new ConcurrentBloomFilter(1_000, 0.001);

        filter.Add("https://example.com/a");

        Assert.True(filter.MightContain("https://example.com/a"));
        Assert.True(filter.MightContain(Utf16Bytes("https://example.com/a")));
    }

    [Fact]
    public void AddedBytesAreReportedAsContained()
    {
        var filter = new ConcurrentBloomFilter(1_000, 0.001);
        byte[] data = [1, 2, 3, 4, 5];

        filter.Add(data);

        Assert.True(filter.MightContain(data));
    }

    [Fact]
    public void TryAddReportsOnlyTheFirstInsertion()
    {
        var filter = new ConcurrentBloomFilter(1_000, 0.001);

        Assert.True(filter.TryAdd("unique"));
        Assert.False(filter.TryAdd("unique"));
        Assert.False(filter.TryAdd(Utf16Bytes("unique")));
    }

    [Fact]
    public void ResetClearsAllBits()
    {
        var filter = new ConcurrentBloomFilter(1_000, 0.001);
        filter.Add("first");
        filter.Add("second");

        filter.Reset();

        Assert.False(filter.MightContain("first"));
        Assert.False(filter.MightContain("second"));
    }

    [Fact]
    public void SaveAndLoadRoundTripPreservesState()
    {
        var original = new ConcurrentBloomFilter(10_000, 0.001);
        for (int i = 0; i < 500; i++)
        {
            original.Add($"https://example.com/{i}");
        }

        using var stream = new MemoryStream();
        original.SaveTo(stream);

        // SaveTo оставляет поток открытым - проверяем, что чтение возможно без пересоздания.
        Assert.True(stream.CanWrite);

        stream.Position = 0;
        ConcurrentBloomFilter restored = ConcurrentBloomFilter.LoadFrom(stream);

        Assert.Equal(original.SizeInBytes, restored.SizeInBytes);
        for (int i = 0; i < 500; i++)
        {
            Assert.True(restored.MightContain($"https://example.com/{i}"));
        }
    }

    [Fact]
    public void LoadedFilterStillHasNoFalseNegativesAfterAdditionalWrites()
    {
        var original = new ConcurrentBloomFilter(1_000, 0.001);
        original.Add("before-save");

        using var stream = new MemoryStream();
        original.SaveTo(stream);
        stream.Position = 0;

        ConcurrentBloomFilter restored = ConcurrentBloomFilter.LoadFrom(stream);
        restored.Add("after-load");

        Assert.True(restored.MightContain("before-save"));
        Assert.True(restored.MightContain("after-load"));
    }

    [Fact]
    public void ObservableFalsePositiveRateStaysWithinLooseBound()
    {
        // Статистическая проверка: фильтр рассчитан на 1% при ёмкости 1000,
        // поэтому при полном заполнении доля ложных срабатываний не должна быть заметно выше.
        const int expectedItems = 1_000;
        const int probes = 20_000;
        const double targetRate = 0.01;
        const double allowedRate = 0.05;

        var filter = new ConcurrentBloomFilter(expectedItems, targetRate);
        for (int i = 0; i < expectedItems; i++)
        {
            filter.Add($"known-{i}");
        }

        int falsePositives = 0;
        for (int i = 0; i < probes; i++)
        {
            if (filter.MightContain($"unknown-{i}"))
            {
                falsePositives++;
            }
        }

        double observedRate = falsePositives / (double)probes;
        Assert.True(
            observedRate <= allowedRate,
            $"Наблюдаемая доля ложных срабатываний {observedRate:P4} превышает допустимую {allowedRate:P4}.");
    }

    [Fact]
    public void SmallFilterWithVeryLowTargetRateDoesNotDegradeToCollapsedProbes()
    {
        // Регрессия: при чётном шаге двойного хеширования и размере карты, кратном 64,
        // k проверок ключа могли схлопнуться в 2-3 позиции. На ёмкости 1000 при цели 1e-9
        // это давало единичные ложные срабатывания на миллионы проверок.
        const int expectedItems = 1_000;
        const int known = 900;
        const int probes = 2_000_000;
        const int allowedFalsePositives = 1;

        var filter = new ConcurrentBloomFilter(expectedItems, 1e-9);
        Span<char> buffer = stackalloc char[32];

        for (int i = 0; i < known; i++)
        {
            int length = WriteKey(buffer, "rot-1-", i);
            filter.Add(buffer[..length]);
        }

        int falsePositives = 0;
        for (int i = 0; i < probes; i++)
        {
            int length = WriteKey(buffer, "probe-", i);
            if (filter.MightContain(buffer[..length]))
            {
                falsePositives++;
            }
        }

        Assert.True(
            falsePositives <= allowedFalsePositives,
            $"Наблюдалось {falsePositives} ложных срабатываний на {probes} проверок при целевой доле 1e-9.");
    }
}