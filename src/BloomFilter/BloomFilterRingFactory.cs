namespace BloomFilter;

/// <summary>
/// Factory for rotating Bloom filter rings.
/// </summary>
public static class BloomFilterRingFactory
{
    /// <summary>
    /// Creates a ring from the supplied configuration values.
    /// </summary>
    /// <param name="filterCount">The number of filters in the ring.</param>
    /// <param name="expectedItems">The expected capacity of each filter.</param>
    /// <param name="falsePositiveRate">The acceptable false-positive probability.</param>
    /// <param name="fillPercent">The fill ratio at which rotation occurs.</param>
    public static BloomFilterRing Create(
        int filterCount,
        long expectedItems,
        double falsePositiveRate = 0.001,
        double fillPercent = 0.9)
    {
        return Create(new BloomFilterRingOptions
        {
            FilterCount = filterCount,
            ExpectedItems = expectedItems,
            FalsePositiveRate = falsePositiveRate,
            FillPercent = fillPercent,
        });
    }

    /// <summary>Creates a ring from a configuration object.</summary>
    public static BloomFilterRing Create(BloomFilterRingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.FilterCount < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.FilterCount,
                "The number of filters in the ring must be at least two.");
        }

        if (options.ExpectedItems <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.ExpectedItems,
                "The expected capacity of the filter must be greater than zero.");
        }

        if (options.FillPercent is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.FillPercent,
                "The fill percentage must be in the range (0, 1].");
        }

        if (options.FalsePositiveRate is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.FalsePositiveRate,
                "The false positive rate must be in the range (0, 1).");
        }

        long rotationThreshold = Math.Max(1, (long)Math.Ceiling(options.ExpectedItems * options.FillPercent));

        return new BloomFilterRing(
            options.FilterCount,
            options.ExpectedItems,
            rotationThreshold,
            options.FalsePositiveRate);
    }
}
