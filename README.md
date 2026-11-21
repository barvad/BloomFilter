# BloomFilter

Потокобезопасная библиотека фильтра Блума для .NET 10 с поддержкой кольцевой ротации фильтров.

## Структура

- `src/BloomFilter` — библиотека `BloomFilter`.
- `tests/BloomFilter.Tests` — модульные и многопоточные тесты.
- `tests/BloomFilter.LoadTests` — консольные нагрузочные сценарии.

## Сборка и тесты

```powershell
dotnet build BloomFilter.slnx --configuration Release
dotnet test BloomFilter.slnx --configuration Release
```

## Использование

```csharp
using BloomFilter;

var filter = new ConcurrentBloomFilter(expectedItems: 1_000_000, falsePositiveRate: 0.001);
filter.Add("https://example.com");

bool mightContain = filter.Contains("https://example.com");
```

Для данных с ограниченным временным окном используйте `BloomFilterRing`, который автоматически ротирует внутренние фильтры.

Подробное описание нагрузочных сценариев находится в [tests/BloomFilter.LoadTests/README.md](tests/BloomFilter.LoadTests/README.md).

## Лицензия

Проект распространяется по лицензии [MIT](LICENSE).
