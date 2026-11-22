using Microsoft.Extensions.DependencyInjection;

namespace BloomFilter;

/// <summary>
/// Dependency injection registration extensions for Bloom filters.
/// </summary>
public static class BloomFilterServiceCollectionExtensions
{
    /// <summary>Registers the standalone Bloom filter factory.</summary>
    public static IServiceCollection AddBloomFilterFactory(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<BloomFilterFactory>();
        services.AddSingleton<IBloomFilterFactory>(provider => provider.GetRequiredService<BloomFilterFactory>());
        return services;
    }

    /// <summary>Registers the rotating Bloom filter ring factory.</summary>
    public static IServiceCollection AddBloomFilterRingFactory(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<BloomFilterRingFactory>();
        services.AddSingleton<IBloomFilterRingFactory>(provider => provider.GetRequiredService<BloomFilterRingFactory>());
        return services;
    }

    /// <summary>
    /// Registers a singleton <see cref="ConcurrentBloomFilter"/> and exposes it as <see cref="IBloomFilter"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="expectedItems">The expected number of unique items.</param>
    /// <param name="falsePositiveRate">The acceptable false-positive probability.</param>
    public static IServiceCollection AddConcurrentBloomFilter(
        this IServiceCollection services,
        long expectedItems,
        double falsePositiveRate = 0.001)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(_ => new ConcurrentBloomFilter(expectedItems, falsePositiveRate));
        services.AddSingleton<IBloomFilter>(provider => provider.GetRequiredService<ConcurrentBloomFilter>());
        return services;
    }

    /// <summary>
    /// Registers a singleton <see cref="BloomFilterRing"/> and exposes it as <see cref="IBloomFilterRing"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The ring configuration.</param>
    public static IServiceCollection AddBloomFilterRing(
        this IServiceCollection services,
        BloomFilterRingOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(_ => new BloomFilterRingFactory().CreateRing(options));
        services.AddSingleton<IBloomFilterRing>(provider => provider.GetRequiredService<BloomFilterRing>());
        return services;
    }

    /// <summary>Registers a singleton rotating Bloom filter ring using default options.</summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddBloomFilterRing(this IServiceCollection services)
    {
        return services.AddBloomFilterRing(new BloomFilterRingOptions());
    }
}
