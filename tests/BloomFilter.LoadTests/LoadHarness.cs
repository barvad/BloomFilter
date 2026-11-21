using System.Diagnostics;

namespace BloomFilter.LoadTests;

/// <summary>
/// Формирует ключи в заранее выделенный буфер без выделений в куче,
/// чтобы замеры отражали стоимость самого фильтра, а не генерации строк.
/// </summary>
internal static class KeyWriter
{
    public static int Write(Span<char> destination, ReadOnlySpan<char> prefix, long value)
    {
        prefix.CopyTo(destination);
        int position = prefix.Length;

        if (!value.TryFormat(destination[position..], out int written))
        {
            throw new InvalidOperationException("Буфер ключа слишком мал.");
        }

        return position + written;
    }
}

internal readonly record struct WorkerResult(long Operations, long Failures, TimeSpan Elapsed, LatencySamples Latency);

/// <summary>
/// Запускает фиксированное число выделенных потоков с общим стартовым барьером,
/// чтобы измерение не искажалось разогревом пула потоков.
/// </summary>
internal static class ParallelRunner
{
    public static WorkerResult[] Run(int threads, Func<int, WorkerContext, WorkerResult> work)
    {
        var context = new WorkerContext();
        var ready = new CountdownEvent(threads);
        using var start = new ManualResetEventSlim(false);
        var results = new WorkerResult[threads];
        var errors = new Exception?[threads];

        var workers = new Thread[threads];
        for (int i = 0; i < threads; i++)
        {
            int id = i;
            workers[i] = new Thread(() =>
            {
                ready.Signal();
                start.Wait();

                try
                {
                    results[id] = work(id, context);
                }
                catch (Exception ex)
                {
                    errors[id] = ex;
                }
            })
            {
                IsBackground = true,
                Name = $"load-worker-{id}",
            };

            workers[i].Start();
        }

        ready.Wait();
        var wall = Stopwatch.StartNew();
        start.Set();

        foreach (Thread worker in workers)
        {
            worker.Join();
        }

        wall.Stop();

        Exception? firstError = errors.FirstOrDefault(e => e is not null);
        if (firstError is not null)
        {
            throw new InvalidOperationException("Поток нагрузки завершился с ошибкой.", firstError);
        }

        return results;
    }

    public static TimeSpan MaxElapsed(WorkerResult[] results)
    {
        TimeSpan max = TimeSpan.Zero;
        foreach (WorkerResult result in results)
        {
            if (result.Elapsed > max)
            {
                max = result.Elapsed;
            }
        }

        return max;
    }

    public static long SumOperations(WorkerResult[] results) => results.Sum(r => r.Operations);

    public static long SumFailures(WorkerResult[] results) => results.Sum(r => r.Failures);

    public static LatencySamples MergeLatency(WorkerResult[] results)
    {
        var merged = new LatencySamples();
        foreach (WorkerResult result in results)
        {
            merged.Merge(result.Latency);
        }

        return merged;
    }
}

internal sealed class WorkerContext
{
    public long StartTicks { get; } = Stopwatch.GetTimestamp();

    public bool Expired(TimeSpan budget)
    {
        return Stopwatch.GetElapsedTime(StartTicks) >= budget;
    }
}