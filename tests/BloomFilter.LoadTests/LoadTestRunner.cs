using System.Diagnostics;
using System.Runtime;

namespace BloomFilter.LoadTests;

internal static class LoadTestRunner
{
    public static int Run(LoadTestOptions options)
    {
        ProfileSettings settings = options.ProfileSettings;

        Console.WriteLine("BloomFilter Load Tests");
        Console.WriteLine($"  Profile      : {options.Profile.ToString().ToLowerInvariant()}");
        Console.WriteLine($"  Threads       : {settings.Threads}");
        Console.WriteLine($"  Processors  : {Environment.ProcessorCount}");
        Console.WriteLine($"  Runtime      : {Environment.Version}");
        Console.WriteLine($"  Server GC    : {(GCSettings.IsServerGC ? "yes" : "no")}");
        Console.WriteLine($"  Scenarios     : {string.Join(", ", LoadTestOptions.KnownScenarios.Where(options.ShouldRun))}");

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
                _ => throw new InvalidOperationException($"Unknown scenario: {scenario}"),
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
                        $"  INSUFFICIENT THROUGHOUT: {report.Title} - " +
                        $"{Fmt.Rate(report.Throughput!.Value)} < {Fmt.Rate(options.MinOpsPerSecond)}");
                }
            }
        }

        bool failed = reports.Any(r => !r.Ok);

        Console.WriteLine();
        Console.WriteLine(
            $"Result: {reports.Count - reports.Count(r => !r.Ok)} out of {reports.Count} scenarios passed, " +
            $"time {Fmt.Duration(total.Elapsed)}");

        if (!throughputOk)
        {
            Console.WriteLine($"Lower throughput boundary {Fmt.Rate(options.MinOpsPerSecond)} not reached.");
        }

        if (failed || !throughputOk)
        {
            Console.WriteLine("RESULT: FAILED");
            return 1;
        }

        Console.WriteLine("RESULT: OK");
        return 0;
    }
}
