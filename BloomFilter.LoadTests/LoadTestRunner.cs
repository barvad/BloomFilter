using System.Diagnostics;
using System.Runtime;

namespace BloomFilter.LoadTests;

internal static class LoadTestRunner
{
    public static int Run(LoadTestOptions options)
    {
        ProfileSettings settings = options.ProfileSettings;

        Console.WriteLine("Нагрузочные тесты BloomFilter");
        Console.WriteLine($"  Профиль      : {options.Profile.ToString().ToLowerInvariant()}");
        Console.WriteLine($"  Потоки       : {settings.Threads}");
        Console.WriteLine($"  Процессоров  : {Environment.ProcessorCount}");
        Console.WriteLine($"  Runtime      : {Environment.Version}");
        Console.WriteLine($"  Server GC    : {(GCSettings.IsServerGC ? "да" : "нет")}");
        Console.WriteLine($"  Сценарии     : {string.Join(", ", LoadTestOptions.KnownScenarios.Where(options.ShouldRun))}");

        var total = Stopwatch.StartNew();
        var reports = new List<ScenarioReport>();

        foreach (string scenario in LoadTestOptions.KnownScenarios)
        {
            if (!options.ShouldRun(scenario))
            {
                continue;
            }

            ScenarioReport report = scenario switch
            {
                "write" => WriteScenarios.RunSingleThreaded(settings),
                "parallel-write" => WriteScenarios.RunParallel(settings),
                "read" => ReadScenarios.RunRead(settings),
                "rotation" => ReadScenarios.RunRotation(settings),
                "fpr" => CapacityScenarios.RunFalsePositiveRate(settings),
                "memory" => CapacityScenarios.RunMemory(settings),
                _ => throw new InvalidOperationException($"Неизвестный сценарий: {scenario}"),
            };

            report.Print();
            reports.Add(report);
        }

        total.Stop();

        bool throughputOk = true;

        if (options.MinOpsPerSecond > 0)
        {
            foreach (ScenarioReport report in reports.Where(r => r.Throughput is not null))
            {
                if (report.Throughput!.Value < options.MinOpsPerSecond)
                {
                    throughputOk = false;
                    Console.WriteLine(
                        $"  НЕДОСТАТОЧНАЯ ПРОПУСКНАЯ СПОСОБНОСТЬ: {report.Title} - " +
                        $"{Fmt.Rate(report.Throughput!.Value)} < {Fmt.Rate(options.MinOpsPerSecond)}");
                }
            }
        }

        bool failed = reports.Any(r => !r.Ok);

        Console.WriteLine();
        Console.WriteLine(
            $"Итог: {reports.Count - reports.Count(r => !r.Ok)} из {reports.Count} сценариев пройдено, " +
            $"время {Fmt.Duration(total.Elapsed)}");

        if (!throughputOk)
        {
            Console.WriteLine($"Нижняя граница пропускной способности {Fmt.Rate(options.MinOpsPerSecond)} не достигнута.");
        }

        if (failed || !throughputOk)
        {
            Console.WriteLine("РЕЗУЛЬТАТ: ПРОВАЛ");
            return 1;
        }

        Console.WriteLine("РЕЗУЛЬТАТ: OK");
        return 0;
    }
}