using System.Text;
using BloomFilter.LoadTests;

Console.OutputEncoding = Encoding.UTF8;

LoadTestOptions options;
try
{
    options = LoadTestOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    LoadTestOptions.PrintHelp();
    return 2;
}

if (options.Help)
{
    LoadTestOptions.PrintHelp();
    return 0;
}

if (options.ListScenarios)
{
    Console.WriteLine("Scenarios:");
    Console.WriteLine("  write           single-threaded write: basic filter and ring without rotation");
    Console.WriteLine("  parallel-write  parallel write without rotation, check for no loss");
    Console.WriteLine("  read            parallel read of the filled ring");
    Console.WriteLine("  rotation        scroll the ring with memory window and counters check");
    Console.WriteLine("  fpr             actual false positive rate on the filled filter");
    Console.WriteLine("  memory          ring volume and allocations per operation");
    return 0;
}

return LoadTestRunner.Run(options);
