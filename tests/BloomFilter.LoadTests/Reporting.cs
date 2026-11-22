using System.Diagnostics;
using System.Globalization;

namespace BloomFilter.LoadTests;

internal static class Fmt
{
    public static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Rate(double opsPerSecond)
    {
        return $"{opsPerSecond.ToString("N0", CultureInfo.InvariantCulture)} ops/s";
    }

    public static string Micros(double value) => $"{value.ToString("N2", CultureInfo.InvariantCulture)} μs";

    public static string Duration(TimeSpan value) => $"{value.TotalSeconds.ToString("N2", CultureInfo.InvariantCulture)} s";

    public static string Bytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = value;
        int unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size.ToString(unit == 0 ? "N0" : "N2", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}

internal sealed class ScenarioReport
{
    private readonly List<(string Label, string Value)> _rows = [];
    private readonly List<string> _notes = [];
    private readonly List<string> _failures = [];

    public ScenarioReport(string title)
    {
        Title = title;
    }

    public string Title { get; }

    public bool Ok => _failures.Count == 0;

    /// <summary>Scenario's main throughput, ops/s (if applicable).</summary>
    public double? Throughput { get; set; }

    public void Row(string label, string value) => _rows.Add((label, value));

    public void Note(string text) => _notes.Add(text);

    public void Check(string description, bool condition, string? details = null)
    {
        if (!condition)
        {
            _failures.Add(details is null ? description : $"{description}: {details}");
        }
    }

    public void Print()
    {
        int width = _rows.Count == 0 ? 0 : _rows.Max(r => r.Label.Length);

        Console.WriteLine();
        Console.WriteLine($"=== {Title} ===");

        foreach ((string label, string value) in _rows)
        {
            Console.WriteLine($"  {label.PadRight(width)} : {value}");
        }

        foreach (string note in _notes)
        {
            Console.WriteLine($"  {note}");
        }

        if (Ok)
        {
            Console.WriteLine("  RESULT: OK");
        }
        else
        {
            foreach (string failure in _failures)
            {
                Console.WriteLine($"  FAILED: {failure}");
            }

            Console.WriteLine("  RESULT: FAILED");
        }
    }

    public static ScenarioReport FromMeasurement(string title, long operations, TimeSpan elapsed)
    {
        var report = new ScenarioReport(title);
        double opsPerSecond = operations / Math.Max(elapsed.TotalSeconds, 1e-9);

        report.Row("Operations", Fmt.N(operations));
        report.Row("Time", Fmt.Duration(elapsed));
        report.Row("Throughput", Fmt.Rate(opsPerSecond));
        return report;
    }
}

internal sealed class LatencySamples
{
    private readonly double[] _batches;

    public LatencySamples(int capacity = 8192)
    {
        _batches = new double[capacity];
    }

    public int Count { get; private set; }

    public void Add(double microseconds)
    {
        if (Count < _batches.Length)
        {
            _batches[Count++] = microseconds;
        }
    }

    public void Merge(LatencySamples other)
    {
        for (int i = 0; i < other.Count; i++)
        {
            Add(other._batches[i]);
        }
    }

    public double Percentile(double percentile)
    {
        if (Count == 0)
        {
            return 0;
        }

        double[] sorted = _batches.AsSpan(0, Count).ToArray();
        Array.Sort(sorted);
        int index = (int)Math.Clamp(Math.Ceiling(percentile * Count) - 1, 0, Count - 1);
        return sorted[index];
    }

    public string Summary()
    {
        return Count == 0
            ? "no data"
            : $"p50 {Fmt.Micros(Percentile(0.50))}, p95 {Fmt.Micros(Percentile(0.95))}, p99 {Fmt.Micros(Percentile(0.99))}";
    }
}

internal sealed class BatchTimer
{
    private readonly long _start = Stopwatch.GetTimestamp();

    public double ElapsedMicroseconds => (Stopwatch.GetTimestamp() - _start) * TicksToMicroseconds;

    private static readonly double TicksToMicroseconds = 1_000_000.0 / Stopwatch.Frequency;

    internal static double TicksToMicrosecondsValue => TicksToMicroseconds;
}
