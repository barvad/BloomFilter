namespace BloomFilter;

/// <summary>
/// Configuration for a rotating Bloom filter ring.
/// </summary>
public sealed class BloomFilterRingOptions
{
    /// <summary>The number of filters in the ring.</summary>
    public int FilterCount { get; init; } = 3;

    /// <summary>The expected capacity of each filter.</summary>
    public long ExpectedItems { get; init; } = 1_000_000;

    /// <summary>The fill ratio at which the active filter is rotated.</summary>
    public double FillPercent { get; init; } = 0.9;

    /// <summary>The acceptable false-positive probability.</summary>
    public double FalsePositiveRate { get; init; } = 0.001;
}
