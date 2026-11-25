using Sales.LoadTest;

try
{
    var options = Options.Parse(args);
    if (options.ShowHelp)
    {
        Console.WriteLine(Options.Help);
        return 0;
    }

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    using var runner = new Runner(options);
    return await runner.RunAsync(cts.Token);
}
catch (Exception ex)
{
    Console.Error.WriteLine("ERROR: " + ex.Message);
    return 1;
}
