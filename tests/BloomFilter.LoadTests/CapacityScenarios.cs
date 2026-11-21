using System.Diagnostics;

namespace BloomFilter.LoadTests;

internal static class CapacityScenarios
{
    private const double TargetRate = 0.001;

    /// <summary>
    /// Фактическая доля ложных срабатываний на фильтре, заполненном до расчётной ёмкости.
    /// </summary>
    public static ScenarioReport RunFalsePositiveRate(ProfileSettings settings)
    {
        int items = settings.FprItems;
        int probes = settings.FprProbes;
        var report = new ScenarioReport(
            $"Ложные срабатывания: {Fmt.N(items)} значений, {Fmt.N(probes)} проверок отсутствующих ключей");

        Span<char> buffer = stackalloc char[32];

        var filter = new ConcurrentBloomFilter(items, TargetRate);
        var fillWatch = Stopwatch.StartNew();
        for (int i = 0; i < items; i++)
        {
            int length = KeyWriter.Write(buffer, "known-", i);
            filter.Add(buffer[..length]);
        }

        fillWatch.Stop();

        long filterFalsePositives = CountFalsePositives(filter, probes);
        double filterRate = filterFalsePositives / (double)probes;

        // То же для кольца: заполняем на одно значение меньше порога, чтобы не проворачиваться.
        BloomFilterRing ring = BloomFilterRingFactory.Create(3, items, TargetRate, fillPercent: 1.0);
        for (int i = 0; i < items - 1; i++)
        {
            int length = KeyWriter.Write(buffer, "ring-known-", i);
            ring.Add(buffer[..length]);
        }

        long ringFalsePositives = CountFalsePositives(ring, probes);
        double ringRate = ringFalsePositives / (double)probes;

        report.Row("Ёмкость фильтра", Fmt.N(items));
        report.Row("Размер базового фильтра", Fmt.Bytes(filter.SizeInBytes));
        report.Row("Целевая доля ложных срабатываний", $"{TargetRate:P3}");
        report.Row("Фактически, базовый фильтр", $"{filterRate:P4} ({Fmt.N(filterFalsePositives)})");
        report.Row("Фактически, кольцо", $"{ringRate:P4} ({Fmt.N(ringFalsePositives)})");
        report.Row("Время заполнения", Fmt.Duration(fillWatch.Elapsed));

        double limit = TargetRate * 2.5 + 0.0005;
        report.Check("Доля ложных срабатываний базового фильтра в допуске", filterRate <= limit, $"{filterRate:P4} > {limit:P4}");
        report.Check("Доля ложных срабатываний кольца в допуске", ringRate <= limit, $"{ringRate:P4} > {limit:P4}");
        report.Check("Кольцо не провернулось", ring.RotationCount == 0, $"ротаций {ring.RotationCount}");
        return report;
    }

    /// <summary>
    /// Объём памяти кольца и отсутствие аллокаций на операцию.
    /// </summary>
    public static ScenarioReport RunMemory(ProfileSettings settings)
    {
        long expected = settings.WriteItems;
        var report = new ScenarioReport($"Память: кольцо на {Fmt.N(expected)} значений на фильтр");

        long before = GC.GetTotalMemory(forceFullCollection: true);
        BloomFilterRing ring = BloomFilterRingFactory.Create(3, expected, 0.001, fillPercent: 1.0);
        long after = GC.GetTotalMemory(forceFullCollection: true);
        long measured = after - before;

        double bitsPerFilter = -(expected * Math.Log(0.001)) / (Math.Log(2) * Math.Log(2));
        long perFilterBytes = (long)Math.Ceiling(bitsPerFilter / 64.0) * sizeof(ulong);
        long theoretical = perFilterBytes * ring.FilterCount;

        report.Row("Управляемая куча до", Fmt.Bytes(before));
        report.Row("Управляемая куча после", Fmt.Bytes(after));
        report.Row("Измеренный размер кольца", Fmt.Bytes(measured));
        report.Row("Теоретический размер кольца", Fmt.Bytes(theoretical));
        report.Row("Байт на значение", $"{(measured / (double)(expected * ring.FilterCount)):N2}");

        report.Check(
            "Измеренный размер близок к теоретическому",
            measured >= theoretical / 2 && measured <= theoretical * 2 + 32L * 1024 * 1024,
            $"измерено {measured} Б, ожидалось около {theoretical} Б");

        long operations = Math.Min(settings.ParallelItems, 2_000_000);
        long capacity = Math.Max(expected, operations);
        var filter = new ConcurrentBloomFilter(capacity, 0.001);
        Span<char> buffer = stackalloc char[32];

        for (int i = 0; i < 20_000; i++)
        {
            int length = KeyWriter.Write(buffer, "warm-", i);
            filter.TryAdd(buffer[..length]);
        }

        int gen0 = GC.CollectionCount(0);
        int gen1 = GC.CollectionCount(1);
        int gen2 = GC.CollectionCount(2);
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();

        for (long i = 0; i < operations; i++)
        {
            int length = KeyWriter.Write(buffer, "mem-", i);
            filter.TryAdd(buffer[..length]);
        }

        for (long i = 0; i < operations; i++)
        {
            int length = KeyWriter.Write(buffer, "mem-", i);
            filter.MightContain(buffer[..length]);
        }

        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

        report.Row("Операций записи и чтения", Fmt.N(operations * 2));
        report.Row("Время операций", Fmt.Duration(watch.Elapsed));
        report.Row("Аллокации кучи на операциях", $"{Fmt.N(allocated)} Б ({allocated / (double)(operations * 2):N4} Б/оп)");
        report.Row(
            "Сборок мусора (0/1/2)",
            $"{GC.CollectionCount(0) - gen0}/{GC.CollectionCount(1) - gen1}/{GC.CollectionCount(2) - gen2}");

        report.Check(
            "Операции фильтра не выделяют память в куче",
            allocated < 64 * 1024,
            $"выделено {Fmt.N(allocated)} Б");
        return report;
    }

    private static long CountFalsePositives(ConcurrentBloomFilter filter, int probes)
    {
        Span<char> buffer = stackalloc char[32];
        long falsePositives = 0;

        for (int i = 0; i < probes; i++)
        {
            int length = KeyWriter.Write(buffer, "probe-", i);
            if (filter.MightContain(buffer[..length]))
            {
                falsePositives++;
            }
        }

        return falsePositives;
    }

    private static long CountFalsePositives(BloomFilterRing ring, int probes)
    {
        Span<char> buffer = stackalloc char[32];
        long falsePositives = 0;

        for (int i = 0; i < probes; i++)
        {
            int length = KeyWriter.Write(buffer, "ring-probe-", i);
            if (ring.MightContain(buffer[..length]))
            {
                falsePositives++;
            }
        }

        return falsePositives;
    }
}