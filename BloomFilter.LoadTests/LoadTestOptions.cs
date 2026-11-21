namespace BloomFilter.LoadTests;

internal enum LoadProfile
{
    Quick,
    Standard,
    Long,
}

internal sealed record ProfileSettings(
    long WriteItems,
    long ParallelItems,
    long ReadItems,
    int Threads,
    int RotationGenerations,
    int FprItems,
    int FprProbes,
    TimeSpan RotationDuration)
{
    public static ProfileSettings For(LoadProfile profile, int threadsOverride)
    {
        int cpu = Environment.ProcessorCount;

        ProfileSettings settings = profile switch
        {
            LoadProfile.Quick => new ProfileSettings(
                WriteItems: 100_000,
                ParallelItems: 1_000_000,
                ReadItems: 1_000_000,
                Threads: Math.Min(4, cpu),
                RotationGenerations: 200,
                FprItems: 50_000,
                FprProbes: 100_000,
                RotationDuration: TimeSpan.FromSeconds(1)),
            LoadProfile.Standard => new ProfileSettings(
                WriteItems: 1_000_000,
                ParallelItems: 5_000_000,
                ReadItems: 5_000_000,
                Threads: Math.Min(cpu, 16),
                RotationGenerations: 1_000,
                FprItems: 100_000,
                FprProbes: 200_000,
                RotationDuration: TimeSpan.FromSeconds(3)),
            LoadProfile.Long => new ProfileSettings(
                WriteItems: 5_000_000,
                ParallelItems: 20_000_000,
                ReadItems: 20_000_000,
                Threads: Math.Min(cpu, 32),
                RotationGenerations: 5_000,
                FprItems: 200_000,
                FprProbes: 400_000,
                RotationDuration: TimeSpan.FromSeconds(10)),
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };

        return threadsOverride > 0 ? settings with { Threads = threadsOverride } : settings;
    }
}

internal sealed class LoadTestOptions
{
    public static readonly string[] KnownScenarios =
    [
        "write",
        "parallel-write",
        "read",
        "rotation",
        "fpr",
        "memory",
    ];

    public HashSet<string> Scenarios { get; } = new(StringComparer.OrdinalIgnoreCase);

    public LoadProfile Profile { get; private set; } = LoadProfile.Quick;

    public int Threads { get; private set; }

    public long MinOpsPerSecond { get; private set; }

    public bool Help { get; private set; }

    public bool ListScenarios { get; private set; }

    public ProfileSettings ProfileSettings => ProfileSettings.For(Profile, Threads);

    public static LoadTestOptions Parse(string[] args)
    {
        var options = new LoadTestOptions();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            switch (arg)
            {
                case "--help" or "-h" or "/?":
                    options.Help = true;
                    break;

                case "--list":
                    options.ListScenarios = true;
                    break;

                case "--profile":
                    options.Profile = ParseProfile(NextValue(args, ref i, arg));
                    break;

                case "--threads":
                    options.Threads = ParsePositiveInt(NextValue(args, ref i, arg), arg);
                    break;

                case "--min-ops":
                    options.MinOpsPerSecond = ParsePositiveLong(NextValue(args, ref i, arg), arg);
                    break;

                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Неизвестный параметр: {arg}");
                    }

                    foreach (string scenario in arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        string name = scenario.Equals("all", StringComparison.OrdinalIgnoreCase)
                            ? "all"
                            : scenario.ToLowerInvariant();

                        if (name != "all" && !KnownScenarios.Contains(name))
                        {
                            throw new ArgumentException($"Неизвестный сценарий: {scenario}");
                        }

                        options.Scenarios.Add(name);
                    }

                    break;
            }
        }

        if (options.Scenarios.Count == 0)
        {
            options.Scenarios.Add("all");
        }

        return options;
    }

    public bool ShouldRun(string scenario)
    {
        return Scenarios.Contains("all") || Scenarios.Contains(scenario);
    }

    private static LoadProfile ParseProfile(string value) => value.ToLowerInvariant() switch
    {
        "quick" => LoadProfile.Quick,
        "standard" => LoadProfile.Standard,
        "long" => LoadProfile.Long,
        _ => throw new ArgumentException($"Неизвестный профиль: {value}. Допустимо: quick, standard, long."),
    };

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Для параметра {option} не указано значение.");
        }

        return args[++index];
    }

    private static int ParsePositiveInt(string value, string option)
    {
        return int.TryParse(value, out int result) && result > 0
            ? result
            : throw new ArgumentException($"Параметр {option} ожидает положительное целое число.");
    }

    private static long ParsePositiveLong(string value, string option)
    {
        return long.TryParse(value, out long result) && result > 0
            ? result
            : throw new ArgumentException($"Параметр {option} ожидает положительное целое число.");
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            Нагрузочные тесты фильтра Блума с ротацией.

            Использование:
              dotnet run -c Release --project BloomFilter.LoadTests -- [сценарии] [параметры]

            Сценарии (можно перечислить через запятую, по умолчанию all):
              write           однопоточная запись и проверка базового фильтра и кольца
              parallel-write  параллельная запись без ротации с проверкой отсутствия потерь
              read            параллельное чтение заранее заполненного кольца
              rotation        детерминированная проверка окна при частых ротациях под нагрузкой
              fpr             фактическая доля ложных срабатываний на заполненном фильтре
              memory          выделение памяти и аллокации на операцию
              all             все сценарии

            Параметры:
              --profile quick|standard|long  объём и длительность (по умолчанию quick)
              --threads N                    число потоков (по умолчанию зависит от профиля)
              --min-ops N                    минимальная пропускная способность, иначе код выхода 1
              --list                         показать сценарии
              --help                         эта справка

            Код выхода: 0 - все проверки пройдены, 1 - нарушен инвариант, 2 - ошибка аргументов.
            """);
    }
}