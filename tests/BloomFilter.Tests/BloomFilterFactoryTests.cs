namespace BloomFilter.Tests;

public sealed class BloomFilterFactoryTests
{
    [Fact]
    public void CreateFilterUsesTheRequestedConfiguration()
    {
        var factory = new BloomFilterFactory();

        ConcurrentBloomFilter filter = factory.CreateFilter(expectedItems: 100, falsePositiveRate: 0.01);

        Assert.True(filter.SizeInBytes > 0);
        filter.Add("value");
        Assert.True(filter.MightContain("value"));
    }

    [Fact]
    public void RingFactoryCreatesRingsThroughTheInterface()
    {
        IBloomFilterRingFactory factory = new BloomFilterRingFactory();

        BloomFilterRing ring = factory.CreateRing(filterCount: 2, expectedItems: 100);

        Assert.Equal(2, ring.FilterCount);
    }
}
