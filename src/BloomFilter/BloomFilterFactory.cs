namespace BloomFilter;

/// <summary>
/// Factory for standalone Bloom filters.
/// </summary>
public sealed class BloomFilterFactory : IBloomFilterFactory
{
    /// <inheritdoc />
    public ConcurrentBloomFilter CreateFilter(long expectedItems, double falsePositiveRate = 0.001)
    {
        return new ConcurrentBloomFilter(expectedItems, falsePositiveRate);
    }
}
