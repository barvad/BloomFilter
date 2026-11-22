namespace BloomFilter;

/// <summary>
/// Factory for rotating Bloom filter rings.
/// </summary>
public sealed class BloomFilterRingFactory : IBloomFilterRingFactory
{
    /// <summary>
    /// Creates a ring from the supplied configuration values.
    /// </summary>
    public BloomFilterRing CreateRing(
        int filterCount,
        long expectedItems,
        double falsePositiveRate = 0.001,
        double fillPercent = 0.9)
    {
        return CreateRing(new BloomFilterRingOptions
        {
            FilterCount = filterCount,
            ExpectedItems = expectedItems,
            FalsePositiveRate = falsePositiveRate,
            FillPercent = fillPercent,
        });
    }

    /// <summary>Creates a ring from a configuration object.</summary>
    public BloomFilterRing CreateRing(BloomFilterRingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.FilterCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.FilterCount, "The number of filters in the ring must be at least two.");
        }

        if (options.ExpectedItems <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ExpectedItems, "The expected capacity of the filter must be greater than zero.");
        }

        if (options.FillPercent is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.FillPercent, "The fill percentage must be in the range (0, 1].");
        }

        if (options.FalsePositiveRate is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.FalsePositiveRate, "The false positive rate must be in the range (0, 1).");
        }

        long rotationThreshold = Math.Max(1, (long)Math.Ceiling(options.ExpectedItems * options.FillPercent));

        return new BloomFilterRing(options.FilterCount, options.ExpectedItems, rotationThreshold, options.FalsePositiveRate);
    }

    /// <summary>Creates a ring using the legacy static API.</summary>
    [Obsolete("Use BloomFilterRingFactory.CreateRing on an instance or IBloomFilterRingFactory instead.")]
    public static BloomFilterRing Create(
        int filterCount,
        long expectedItems,
        double falsePositiveRate = 0.001,
        double fillPercent = 0.9)
    {
        return new BloomFilterRingFactory().CreateRing(filterCount, expectedItems, falsePositiveRate, fillPercent);
    }

    /// <summary>Creates a ring using the legacy static API.</summary>
    [Obsolete("Use BloomFilterRingFactory.CreateRing on an instance or IBloomFilterRingFactory instead.")]
    public static BloomFilterRing Create(BloomFilterRingOptions options)
    {
        return new BloomFilterRingFactory().CreateRing(options);
    }
}
