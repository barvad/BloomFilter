# BloomFilter

A thread-safe Bloom filter library for .NET 10 with support for rotating filter rings.

## Structure

- `src/BloomFilter` — the `barvad.RotatingBloomFilter` library.
- `tests/BloomFilter.Tests` — unit and multithreaded tests.
- `tests/BloomFilter.LoadTests` — console load test scenarios.

## Building and Testing

```powershell
dotnet build BloomFilter.slnx --configuration Release
dotnet test BloomFilter.slnx --configuration Release
```

## Usage

```csharp
using BloomFilter;

var filter = new ConcurrentBloomFilter(expectedItems: 1_000_000, falsePositiveRate: 0.001);
filter.Add("https://example.com");

bool mightContain = filter.MightContain("https://example.com");
```

For data with a limited time window, use `BloomFilterRing`, which automatically rotates the internal filters.

## Interfaces and dependency injection

`ConcurrentBloomFilter` implements `IBloomFilter`, and `BloomFilterRing` implements `IBloomFilterRing`. Existing concrete classes and factory methods remain available, so this is a backward-compatible minor release.

Install the package and register a singleton filter or ring:

```csharp
using BloomFilter;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection()
    .AddConcurrentBloomFilter(expectedItems: 1_000_000)
    .AddBloomFilterRing(new BloomFilterRingOptions
    {
        FilterCount = 3,
        ExpectedItems = 1_000_000,
        FillPercent = 0.9,
        FalsePositiveRate = 0.001,
    });
```

The extension methods register both the concrete type and its interface. The package uses `Microsoft.Extensions.DependencyInjection.Abstractions` and does not require a specific application host.

Detailed load test scenarios are described in [tests/BloomFilter.LoadTests/README.md](tests/BloomFilter.LoadTests/README.md).

## NuGet package

The package is named `barvad.RotatingBloomFilter`. Version `1.1.0` adds the public interfaces and dependency injection extensions without removing or changing existing public APIs.

## Publishing to NuGet.org

Releases are published by `.github/workflows/publish-nuget.yml` using NuGet Trusted Publishing (GitHub OIDC), without a long-lived API key.

Create this trusted publishing policy in the `barvad` NuGet.org account:

- Repository owner: `barvad`
- Repository: `BloomFilter`
- Workflow file: `publish-nuget.yml`
- Environment: leave empty

The workflow publishes the `barvad.RotatingBloomFilter` library package when a GitHub release is published. Test projects are not included in the package.

## License

This project is licensed under the [MIT](LICENSE) license.
