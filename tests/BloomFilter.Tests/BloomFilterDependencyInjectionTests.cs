using Microsoft.Extensions.DependencyInjection;

namespace BloomFilter.Tests;

public sealed class BloomFilterDependencyInjectionTests
{
    [Fact]
    public void AddBloomFilterFactoriesRegistersBothFactoryInterfaces()
    {
        var services = new ServiceCollection()
            .AddBloomFilterFactory()
            .AddBloomFilterRingFactory();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<BloomFilterFactory>(provider.GetRequiredService<IBloomFilterFactory>());
        Assert.IsType<BloomFilterRingFactory>(provider.GetRequiredService<IBloomFilterRingFactory>());
    }

    [Fact]
    public void AddConcurrentBloomFilterRegistersTheConcreteFilterAndInterface()
    {
        var services = new ServiceCollection()
            .AddConcurrentBloomFilter(expectedItems: 100);

        using ServiceProvider provider = services.BuildServiceProvider();

        var filter = provider.GetRequiredService<IBloomFilter>();
        var concrete = provider.GetRequiredService<ConcurrentBloomFilter>();

        Assert.Same(concrete, filter);
    }

    [Fact]
    public void AddBloomFilterRingRegistersTheConcreteRingAndInterface()
    {
        var services = new ServiceCollection()
            .AddBloomFilterRing(new BloomFilterRingOptions
            {
                ExpectedItems = 100,
            });

        using ServiceProvider provider = services.BuildServiceProvider();

        var ring = provider.GetRequiredService<IBloomFilterRing>();
        var concrete = provider.GetRequiredService<BloomFilterRing>();

        Assert.Same(concrete, ring);
        Assert.Equal(100 * 0.9, concrete.RotationThreshold);
    }
}
