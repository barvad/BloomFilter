using System.Diagnostics;

namespace BloomFilter.LoadTests;

internal static class WriteScenarios
{
    private const int BatchSize = 1024;

    /// <summary>
    /// Однопоточная запись в базовый фильтр и в кольцо без ротации.
    /// </summary>
    public static ScenarioReport RunSingleThreaded(ProfileSettings settings)
    {
        long items = settings.WriteItems;
        var report = new ScenarioReport($"Однопоточная запись: {Fmt.N(items)} уникальных значений");

        Span<char> buffer = stackalloc char[32];

        // Разогрев выполняется на отдельных экземплярах, чтобы не засорять измеряемое кольцо.
        WarmUp(buffer, 10_000);

        var filter = new ConcurrentBloomFilter(items, 0.001);
        BloomFilterRing ring = BloomFilterRingFactory.Create(3, items + 1, 0.001, fillPercent: 1.0);

        var filterLatency = new LatencySamples();
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var filterWatch = Stopwatch.StartNew();

        long batchStart = Stopwatch.GetTimestamp();
        for (long i = 0; i < items; i++)
        {
            int length = KeyWriter.Write(buffer, "single-", i);
            filter.Add(buffer[..length]);

            if ((i & (BatchSize - 1)) == BatchSize - 1)
            {
                filterLatency.Add(BatchTimer.TicksToMicrosecondsValue * (Stopwatch.GetTimestamp() - batchStart) / BatchSize);
                batchStart = Stopwatch.GetTimestamp();
            }
        }

        filterWatch.Stop();
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();

        var ringLatency = new LatencySamples();
        var ringWatch = Stopwatch.StartNew();

        batchStart = Stopwatch.GetTimestamp();
        for (long i = 0; i < items; i++)
        {
            int length = KeyWriter.Write(buffer, "ring-", i);
            ring.Add(buffer[..length]);

            if ((i & (BatchSize - 1)) == BatchSize - 1)
            {
                ringLatency.Add(BatchTimer.TicksToMicrosecondsValue * (Stopwatch.GetTimestamp() - batchStart) / BatchSize);
                batchStart = Stopwatch.GetTimestamp();
            }
        }

        ringWatch.Stop();

        double filterRate = items / filterWatch.Elapsed.TotalSeconds;
        double ringRate = items / ringWatch.Elapsed.TotalSeconds;
        long allocated = allocAfter - allocBefore;

        report.Row("Add, базовый фильтр", Fmt.Rate(filterRate));
        report.Row("Add, кольцо (без ротации)", Fmt.Rate(ringRate));
        report.Row("Задержка Add, базовый фильтр", filterLatency.Summary());
        report.Row("Задержка Add, кольцо", ringLatency.Summary());
        report.Row("Аллокации в куче", $"{Fmt.N(allocated)} Б ({allocated / (double)items:N4} Б/оп)");
        report.Row("Учтено уникальных, кольцо", $"{Fmt.N(ring.TotalUniqueCount)} из {Fmt.N(items)}");
        report.Row("Ротаций", Fmt.N(ring.RotationCount));

        report.Check("Ротации не ожидались", ring.RotationCount == 0, $"получено {ring.RotationCount}");
        report.Check(
            "Учтено не менее 99% уникальных значений",
            ring.TotalUniqueCount >= (long)(items * 0.99),
            $"учтено {ring.TotalUniqueCount}");
        report.Check(
            "Запись не выделяет память в куче",
            allocated < 64 * 1024,
            $"выделено {allocated} Б");

        // Биты только накапливаются, поэтому каждое переданное значение должно находиться.
        long sampled = 0;
        long present = 0;
        for (long i = 0; i < items; i += Math.Max(1, items / 10_000))
        {
            int length = KeyWriter.Write(buffer, "single-", i);
            sampled++;
            if (filter.MightContain(buffer[..length]))
            {
                present++;
            }
        }

        report.Check("Нет ложных отрицаний в базовом фильтре", present == sampled, $"{present} из {sampled}");
        report.Throughput = ringRate;
        return report;
    }

    /// <summary>
    /// Параллельная запись в кольцо, размер которого заведомо исключает ротацию:
    /// проверяем, что под нагрузкой не теряется ни одно переданное значение.
    /// </summary>
    public static ScenarioReport RunParallel(ProfileSettings settings)
    {
        long total = settings.ParallelItems;
        int threads = settings.Threads;

        // Порог на единицу больше общего числа записей, поэтому окно не проворачивается.
        BloomFilterRing ring = BloomFilterRingFactory.Create(3, total + 1, 0.001, fillPercent: 1.0);
        var report = new ScenarioReport(
            $"Параллельная запись без ротации: {Fmt.N(total)} значений, {threads} потоков");

        long baseBudget = total / threads;

        var wall = Stopwatch.StartNew();
        WorkerResult[] results = ParallelRunner.Run(threads, (thread, _) =>
        {
            long start = thread * baseBudget;
            long end = thread == threads - 1 ? total : start + baseBudget;
            long added = 0;
            long failures = 0;
            var latency = new LatencySamples();
            Span<char> buffer = stackalloc char[32];
            var workerWatch = Stopwatch.StartNew();

            long batchStart = Stopwatch.GetTimestamp();
            long inBatch = 0;
            for (long i = start; i < end; i++)
            {
                int length = KeyWriter.Write(buffer, "par-", i);

                if (ring.TryAdd(buffer[..length]))
                {
                    added++;
                }
                else
                {
                    failures++;
                }

                if (++inBatch == BatchSize)
                {
                    latency.Add(BatchTimer.TicksToMicrosecondsValue * (Stopwatch.GetTimestamp() - batchStart) / BatchSize);
                    batchStart = Stopwatch.GetTimestamp();
                    inBatch = 0;
                }
            }

            workerWatch.Stop();
            return new WorkerResult(added, failures, workerWatch.Elapsed, latency);
        });

        wall.Stop();

        long successes = ParallelRunner.SumOperations(results);
        long rejected = ParallelRunner.SumFailures(results);
        double rate = successes / wall.Elapsed.TotalSeconds;

        report.Row("Принято уникальных", Fmt.N(successes));
        report.Row("Отклонено как известные", Fmt.N(rejected));
        report.Row("Время", Fmt.Duration(wall.Elapsed));
        report.Row("Пропускная способность", Fmt.Rate(rate));
        report.Row("Задержка записи", ParallelRunner.MergeLatency(results).Summary());
        report.Row("Всего учтено кольцом", Fmt.N(ring.TotalUniqueCount));
        report.Row("Ротаций", Fmt.N(ring.RotationCount));

        report.Check("Ротации не ожидались", ring.RotationCount == 0, $"получено {ring.RotationCount}");
        report.Check(
            "Счётчик кольца совпадает с числом принятых значений",
            ring.TotalUniqueCount == successes,
            $"кольцо {ring.TotalUniqueCount}, принято {successes}");
        report.Check(
            "Отклонений не больше 1% (ложные срабатывания)",
            rejected <= total / 100,
            $"отклонено {rejected}");

        // В невращающемся кольце все переданные ключи должны читаться как присутствующие.
        long missing = 0;
        long sampled = 0;
        Span<char> check = stackalloc char[32];
        for (long i = 0; i < total; i += Math.Max(1, total / 20_000))
        {
            int length = KeyWriter.Write(check, "par-", i);
            sampled++;
            if (!ring.MightContain(check[..length]))
            {
                missing++;
            }
        }

        report.Check("Нет ложных отрицаний", missing == 0, $"отсутствует {missing} из {sampled}");
        report.Throughput = rate;
        return report;
    }

    private static void WarmUp(Span<char> buffer, int count)
    {
        // Одноразовые экземпляры: важны только прогрев JIT и веток кода.
        var filter = new ConcurrentBloomFilter(count * 2, 0.001);
        BloomFilterRing ring = BloomFilterRingFactory.Create(3, count * 2, 0.001, fillPercent: 1.0);

        for (int i = 0; i < count; i++)
        {
            int length = KeyWriter.Write(buffer, "warmup-", i);
            filter.Add(buffer[..length]);
            ring.Add(buffer[..length]);
        }
    }
}