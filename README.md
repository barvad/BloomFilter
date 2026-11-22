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

## License

This project is licensed under the [MIT](LICENSE) license.
