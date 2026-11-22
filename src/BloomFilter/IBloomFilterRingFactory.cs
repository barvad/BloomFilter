namespace BloomFilter;

/// <summary>
/// Creates rotating Bloom filter rings.
/// </summary>
public interface IBloomFilterRingFactory
{
    /// <summary>Creates a ring from individual configuration values.</summary>
    BloomFilterRing CreateRing(
        int filterCount,
        long expectedItems,
        double falsePositiveRate = 0.001,
        double fillPercent = 0.9);

    /// <summary>Creates a ring from configuration options.</summary>
    BloomFilterRing CreateRing(BloomFilterRingOptions options);
}
