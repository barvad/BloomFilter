# BloomFilter

A thread-safe Bloom filter library for .NET 10 with support for filter ring rotation.

## Structure

- `src/BloomFilter` — the `BloomFilter` library.
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

bool mightContain = filter.Contains("https://example.com");
```

For data with a limited time window, use `BloomFilterRing`, which automatically rotates the internal filters.

Detailed load test scenarios are described in [tests/BloomFilter.LoadTests/README.md](tests/BloomFilter.LoadTests/README.md).

## Publishing to NuGet.org

Releases are published by `.github/workflows/publish-nuget.yml` using NuGet Trusted Publishing (GitHub OIDC), without a long-lived API key.

Create this trusted publishing policy in the `barvad` NuGet.org account:

- Repository owner: `barvad`
- Repository: `BloomFilter`
- Workflow file: `publish-nuget.yml`
- Environment: leave empty

The workflow publishes the `BloomFilter` library package when a GitHub release is published. Test projects are not included in the package.

## License

This project is licensed under the [MIT](LICENSE) license.
