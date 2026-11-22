namespace BloomFilter;

/// <summary>
/// Creates standalone Bloom filters.
/// </summary>
public interface IBloomFilterFactory
{
    /// <summary>Creates a thread-safe Bloom filter.</summary>
    ConcurrentBloomFilter CreateFilter(long expectedItems, double falsePositiveRate = 0.001);
}
