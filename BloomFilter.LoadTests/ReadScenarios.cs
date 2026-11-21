using System.Diagnostics;

namespace BloomFilter.LoadTests;

internal static class ReadScenarios
{
    private const int BatchSize = 4096;

    /// <summary>
    /// Параллельное чтение кольца, заполненного до порога ротации.
    /// </summary>
    public static ScenarioReport RunRead(ProfileSettings settings)
    {
        long total = settings.ReadItems;
        int threads = settings.Threads;
        BloomFilterRing ring = BloomFilterRingFactory.Create(3, total + 1, 0.001, fillPercent: 1.0);
        var report = new ScenarioReport($"Параллельное чтение: {Fmt.N(total)} ключей, {threads} потоков");

        long perThread = total / threads;

        var fillWatch = Stopwatch.StartNew();
        ParallelRunner.Run(threads, (thread, _) =>
        {
            long start = thread * perThread;
            long end = thread == threads - 1 ? total : start + perThread;
            Span<char> buffer = stackalloc char[32];

            for (long i = start; i < end; i++)
            {
                int length = KeyWriter.Write(buffer, "read-", i);
                ring.Add(buffer[..length]);
            }

            return new WorkerResult(end - start, 0, TimeSpan.Zero, new LatencySamples());
        });
        fillWatch.Stop();

        TimeSpan budget = settings.RotationDuration;
        var readWatch = Stopwatch.StartNew();
        WorkerResult[] results = ParallelRunner.Run(threads, (thread, context) =>
            {
                long start = thread * perThread;
                long end = thread == threads - 1 ? total : start + perThread;
                long reads = 0;
                long missing = 0;
                var latency = new LatencySamples();
                Span<char> buffer = stackalloc char[32];
                var workerWatch = Stopwatch.StartNew();

                long batchStart = Stopwatch.GetTimestamp();
                long inBatch = 0;

                do
                {
                    for (long i = start; i < end; i++)
                    {
                        int length = KeyWriter.Write(buffer, "read-", i);
                        if (!ring.MightContain(buffer[..length]))
                        {
                            missing++;
                        }

                        reads++;

                        if (++inBatch == BatchSize)
                        {
                            latency.Add(BatchTimer.TicksToMicrosecondsValue * (Stopwatch.GetTimestamp() - batchStart) / BatchSize);
                            batchStart = Stopwatch.GetTimestamp();
                            inBatch = 0;
                        }
                    }
                }
                while (!context.Expired(budget));

                workerWatch.Stop();
                return new WorkerResult(reads, missing, workerWatch.Elapsed, latency);
            });
        readWatch.Stop();

        long readsTotal = ParallelRunner.SumOperations(results);
        long missingTotal = ParallelRunner.SumFailures(results);
        double readRate = readsTotal / readWatch.Elapsed.TotalSeconds;

        report.Row("Заполнение", $"{Fmt.Rate(total / fillWatch.Elapsed.TotalSeconds)}, {Fmt.Duration(fillWatch.Elapsed)}");
        report.Row("Прочитано", Fmt.N(readsTotal));
        report.Row("Пропускная способность чтения", Fmt.Rate(readRate));
        report.Row("Задержка чтения", ParallelRunner.MergeLatency(results).Summary());
        report.Row("Ложных отрицаний", Fmt.N(missingTotal));
        report.Row("Ротаций", Fmt.N(ring.RotationCount));

        report.Check("Ротации не ожидались", ring.RotationCount == 0, $"получено {ring.RotationCount}");
        report.Check("Нет ложных отрицаний при чтении", missingTotal == 0, $"пропущено {missingTotal}");
        report.Check(
            "Заполнено не менее 99% ключей",
            ring.TotalUniqueCount >= (long)(total * 0.99),
            $"учтено {ring.TotalUniqueCount}");

        report.Throughput = readRate;
        return report;
    }

    /// <summary>
    /// Прокрутка кольца под нагрузкой с проверкой окна памяти.
    /// Последние поколения должны читаться, а поколения, чьи фильтры переиспользованы, - нет.
    /// </summary>
    /// <remarks>
    /// Кольцо намеренно маленькое, поэтому изредка возможны ложные срабатывания
    /// (особенно при очень низком FPR и малом размере битовой карты). Они не ломают
    /// инварианты: счётчики считаются по фактически принятым значениям, а граница
    /// окна проверяется с запасом в одно поколение.
    /// </remarks>
    public static ScenarioReport RunRotation(ProfileSettings settings)
    {
        const long expectedItems = 10_000;
        const double falsePositiveRate = 1e-9;
        const int filterCount = 3;
        const int sampleSize = 64;
        const int sampleSlots = 5;

        BloomFilterRing ring = BloomFilterRingFactory.Create(
            filterCount,
            expectedItems,
            falsePositiveRate,
            fillPercent: 0.9);

        long threshold = ring.RotationThreshold;
        int generations = settings.RotationGenerations;
        long totalOps = generations * threshold;

        var report = new ScenarioReport(
            $"Ротация под нагрузкой: {Fmt.N(generations)} поколений по {Fmt.N(threshold)} значений");

        var samples = new string[sampleSlots][];
        var latency = new LatencySamples();
        long accepted = 0;
        long rejected = 0;
        long rotationFailures = 0;
        long windowFailures = 0;
        long totalAddTicks = 0;

        Span<char> buffer = stackalloc char[48];

        for (int generation = 1; generation <= generations; generation++)
        {
            int slot = (generation - 1) % sampleSlots;
            samples[slot] = BuildSamples(generation, threshold, sampleSize);

            string prefix = $"rot-{generation}-";
            long generationStart = Stopwatch.GetTimestamp();

            for (long i = 0; i < threshold; i++)
            {
                int length = KeyWriter.Write(buffer, prefix, i);
                if (ring.TryAdd(buffer[..length]))
                {
                    accepted++;
                }
                else
                {
                    rejected++;
                }
            }

            long generationTicks = Stopwatch.GetTimestamp() - generationStart;
            totalAddTicks += generationTicks;
            latency.Add(BatchTimer.TicksToMicrosecondsValue * generationTicks / threshold);

            // Ротация происходит ровно на каждом пороге принятых значений.
            long expectedRotations = accepted / threshold;

            if (ring.RotationCount != expectedRotations || ring.TotalUniqueCount != accepted)
            {
                rotationFailures++;
            }

            if (ring.ActiveIndex != (int)(expectedRotations % filterCount))
            {
                rotationFailures++;
            }

            // Текущее и предыдущее поколения должны находиться в кольце.
            if (!AllPresent(ring, samples[slot], expected: true))
            {
                windowFailures++;
            }

            if (generation >= 2 && !AllPresent(ring, samples[(generation - 2) % sampleSlots], expected: true))
            {
                windowFailures++;
            }

            // Поколение g-2 переиспользуется на этой ротации: допускаем редкие ложные
            // срабатывания после очистки, но не более 5% выборки.
            if (generation >= 3 && PresentCount(ring, samples[(generation - 3) % sampleSlots]) > sampleSize / 20)
            {
                windowFailures++;
            }

            // Поколение g-3 покинуло окно гарантированно, даже если граница сдвинулась.
            if (generation >= 4 && !AllPresent(ring, samples[(generation - 4) % sampleSlots], expected: false))
            {
                windowFailures++;
            }
        }

        double totalAddSeconds = totalAddTicks / (double)Stopwatch.Frequency;
        double rate = totalOps / totalAddSeconds;

        report.Row("Операций добавления", Fmt.N(totalOps));
        report.Row("Принято уникальных", Fmt.N(accepted));
        report.Row("Отклонено ложными срабатываниями", Fmt.N(rejected));
        report.Row("Время добавления", Fmt.Duration(TimeSpan.FromSeconds(totalAddSeconds)));
        report.Row("Пропускная способность", Fmt.Rate(rate));
        report.Row("Задержка добавления", latency.Summary());
        report.Row("Ротаций", Fmt.N(ring.RotationCount));
        report.Row("Активный индекс", ring.ActiveIndex.ToString());
        report.Row("Учтено уникальных", Fmt.N(ring.TotalUniqueCount));

        report.Check(
            "Доля отказов не выше 0,1%",
            rejected <= totalOps / 1_000,
            $"отклонено {rejected} из {totalOps}");
        report.Check(
            "Счётчики ротаций и уникальных совпадают с принятыми значениями",
            rotationFailures == 0,
            $"расхождений {rotationFailures}");
        report.Check(
            "Окно памяти соответствует кольцу из трёх фильтров",
            windowFailures == 0,
            $"нарушений {windowFailures}");

        report.Throughput = rate;
        return report;
    }

    private static string[] BuildSamples(int generation, long threshold, int sampleSize)
    {
        var result = new string[sampleSize];
        long step = Math.Max(1, threshold / sampleSize);

        for (int i = 0; i < sampleSize; i++)
        {
            long value = (i * step) % threshold;
            result[i] = $"rot-{generation}-{value}";
        }

        return result;
    }

    private static bool AllPresent(BloomFilterRing ring, string[] values, bool expected)
    {
        foreach (string value in values)
        {
            if (ring.MightContain(value) != expected)
            {
                return false;
            }
        }

        return true;
    }

    private static int PresentCount(BloomFilterRing ring, string[] values)
    {
        int present = 0;

        foreach (string value in values)
        {
            if (ring.MightContain(value))
            {
                present++;
            }
        }

        return present;
    }
}