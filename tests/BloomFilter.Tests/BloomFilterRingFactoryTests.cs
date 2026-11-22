namespace BloomFilter.Tests;

public sealed class BloomFilterRingFactoryTests
{
    [Fact]
    public void OptionsHaveDocumentedDefaults()
    {
        var options = new BloomFilterRingOptions();

        Assert.Equal(3, options.FilterCount);
        Assert.Equal(1_000_000L, options.ExpectedItems);
        Assert.Equal(0.9, options.FillPercent);
        Assert.Equal(0.001, options.FalsePositiveRate);
    }

    [Fact]
    public void CreateRejectsNullOptions()
    {
        Assert.Throws<ArgumentNullException>(() => new BloomFilterRingFactory().CreateRing(null!));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-3)]
    public void CreateRejectsRingWithLessThanTwoFilters(int filterCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BloomFilterRingFactory().CreateRing(filterCount, expectedItems: 100));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(-10_000L)]
    public void CreateRejectsNonPositiveExpectedItems(long expectedItems)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BloomFilterRingFactory().CreateRing(filterCount: 3, expectedItems));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(1.0001)]
    [InlineData(2.0)]
    public void CreateRejectsFillPercentOutsideUnitInterval(double fillPercent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BloomFilterRingFactory().CreateRing(3, 100, fillPercent: fillPercent));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void CreateRejectsFalsePositiveRateOutsideOpenUnitInterval(double falsePositiveRate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BloomFilterRingFactory().CreateRing(3, 100, falsePositiveRate));
    }

    [Fact]
    public void CreateAcceptsBoundaryValues()
    {
        BloomFilterRing ring = new BloomFilterRingFactory().CreateRing(2, 100, 0.5, fillPercent: 1.0);

        Assert.Equal(2, ring.FilterCount);
        Assert.Equal(100L, ring.RotationThreshold);
    }

    [Fact]
    public void CreateFromOptionsAndFromArgumentsProduceSameConfiguration()
    {
        var options = new BloomFilterRingOptions
        {
            FilterCount = 4,
            ExpectedItems = 2_000,
            FillPercent = 0.75,
            FalsePositiveRate = 0.001,
        };

        BloomFilterRing fromOptions = new BloomFilterRingFactory().CreateRing(options);
        BloomFilterRing fromArguments = new BloomFilterRingFactory().CreateRing(4, 2_000, 0.001, 0.75);

        Assert.Equal(fromArguments.FilterCount, fromOptions.FilterCount);
        Assert.Equal(fromArguments.RotationThreshold, fromOptions.RotationThreshold);
    }

    [Theory]
    [InlineData(1_000L, 0.9, 900L)]
    [InlineData(1_000L, 0.5, 500L)]
    [InlineData(7L, 0.5, 4L)]      // rounding up: 3.5 -> 4
    [InlineData(10L, 0.01, 1L)]
    [InlineData(1L, 0.001, 1L)]    // threshold cannot be zero
    public void RotationThresholdIsCeilingOfExpectedItemsTimesFillPercent(
        long expectedItems,
        double fillPercent,
        long expectedThreshold)
    {
        BloomFilterRing ring = new BloomFilterRingFactory().CreateRing(3, expectedItems, fillPercent: fillPercent);

        Assert.Equal(expectedThreshold, ring.RotationThreshold);
    }
}

