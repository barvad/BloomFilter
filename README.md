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

`ConcurrentBloomFilter` implements `IBloomFilter`, and `BloomFilterRing` implements `IBloomFilterRing`. The injectable `BloomFilterFactory` implements `IBloomFilterFactory`, while `BloomFilterRingFactory` implements `IBloomFilterRingFactory`.

New code can create filters and rings through interfaces:

```csharp
IBloomFilterFactory filterFactory = new BloomFilterFactory();
IBloomFilterRingFactory ringFactory = new BloomFilterRingFactory();

IBloomFilter filter = filterFactory.CreateFilter(expectedItems: 1_000_000);
IBloomFilterRing ring = ringFactory.CreateRing(filterCount: 3, expectedItems: 1_000_000);
```

The old static `BloomFilterRingFactory.Create(...)` methods remain available and are marked obsolete. Use instance `CreateRing(...)` methods instead.

Install the package and register the filters and factories:

```csharp
using BloomFilter;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection()
    .AddBloomFilterFactory()
    .AddBloomFilterRingFactory()
    .AddConcurrentBloomFilter(expectedItems: 1_000_000)
    .AddBloomFilterRing(new BloomFilterRingOptions
    {
        FilterCount = 3,
        ExpectedItems = 1_000_000,
        FillPercent = 0.9,
        FalsePositiveRate = 0.001,
    });
```

The extension methods register singleton factories and filters, exposing both concrete types and their interfaces. The package uses `Microsoft.Extensions.DependencyInjection.Abstractions` and does not require a specific application host.

Detailed load test scenarios are described in [tests/BloomFilter.LoadTests/README.md](tests/BloomFilter.LoadTests/README.md).

## NuGet package

The package is named `barvad.RotatingBloomFilter`. Version `1.2.1` adds the NuGet package icon from `assets/icon.png`. Existing static `BloomFilterRingFactory.Create(...)` methods remain available as obsolete compatibility shims.

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
