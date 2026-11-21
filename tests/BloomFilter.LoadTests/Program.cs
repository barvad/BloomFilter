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
    Console.WriteLine("Сценарии:");
    Console.WriteLine("  write           однопоточная запись: базовый фильтр и кольцо без ротации");
    Console.WriteLine("  parallel-write  параллельная запись без ротации, проверка отсутствия потерь");
    Console.WriteLine("  read            параллельное чтение заполненного кольца");
    Console.WriteLine("  rotation        прокрутка кольца с проверкой окна памяти и счётчиков");
    Console.WriteLine("  fpr             фактическая доля ложных срабатываний на заполненном фильтре");
    Console.WriteLine("  memory          объём кольца и аллокации на операцию");
    return 0;
}

return LoadTestRunner.Run(options);