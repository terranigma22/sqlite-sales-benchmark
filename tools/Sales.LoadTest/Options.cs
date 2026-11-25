using System.Text.Json.Serialization;
using Sales.Contracts;

namespace Sales.LoadTest;

public sealed class Options
{
    public int Cashiers { get; set; } = 200;
    public int SalesPerCashier { get; set; } = 500;
    public string Url { get; set; } = "http://localhost:5080";
    public string Output { get; set; } = "results";
    public bool Reset { get; set; } = true;
    public int WarmupSales { get; set; }
    public int TimeoutSeconds { get; set; } = 180;
    public int SampleIntervalMs { get; set; } = 250;
    public int Inflight { get; set; } = 1;
    public bool NoCsv { get; set; }
    public string Label { get; set; } = "";
    public bool Open { get; set; }

    [JsonIgnore]
    public bool ShowHelp { get; set; }

    public static Options Parse(string[] args)
    {
        var options = new Options();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Next()
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"{arg} necesita un valor.");
                return args[++i];
            }

            switch (arg)
            {
                case "--cashiers":
                    options.Cashiers = int.Parse(Next());
                    break;
                case "--sales-per-cashier":
                    options.SalesPerCashier = int.Parse(Next());
                    break;
                case "--url":
                    options.Url = Next();
                    break;
                case "--output":
                    options.Output = Next();
                    break;
                case "--warmup":
                    options.WarmupSales = int.Parse(Next());
                    break;
                case "--timeout":
                    options.TimeoutSeconds = int.Parse(Next());
                    break;
                case "--sample-ms":
                    options.SampleIntervalMs = int.Parse(Next());
                    break;
                case "--label":
                    options.Label = Next();
                    break;
                case "--inflight":
                    options.Inflight = int.Parse(Next());
                    break;
                case "--no-csv":
                    options.NoCsv = true;
                    break;
                case "--no-reset":
                    options.Reset = false;
                    break;
                case "--open":
                    options.Open = true;
                    break;
                case "-h":
                case "--help":
                    options.ShowHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Argumento desconocido: {arg}. Usa --help.");
            }
        }

        if (options.Cashiers <= 0)
            throw new ArgumentException("--cashiers debe ser > 0.");
        if (options.SalesPerCashier <= 0)
            throw new ArgumentException("--sales-per-cashier debe ser > 0.");
        if (options.SalesPerCashier >= ClientSaleIds.CashierMultiplier)
            throw new ArgumentException(
                $"--sales-per-cashier debe ser < {ClientSaleIds.CashierMultiplier:N0} " +
                "(el id codifica cajero y secuencia en un solo entero).");
        if ((long)options.Cashiers * options.SalesPerCashier > int.MaxValue)
            throw new ArgumentException("El total de ventas no cabe en un int.");
        if (!Uri.TryCreate(options.Url, UriKind.Absolute, out _))
            throw new ArgumentException("--url debe ser una URL absoluta.");
        if (options.TimeoutSeconds <= 0)
            throw new ArgumentException("--timeout debe ser > 0.");
        if (options.SampleIntervalMs < 50)
            throw new ArgumentException("--sample-ms debe ser >= 50.");
        if (options.Inflight < 1)
            throw new ArgumentException("--inflight debe ser >= 1.");

        return options;
    }

    public static string Help => """
        Prueba de carga SQLite + EF Core.

        Uso:
          dotnet run --project tools/Sales.LoadTest -- [opciones]

        Opciones:
          --cashiers N             Cajeros concurrentes (default 200)
          --sales-per-cashier N    Ventas por cajero (default 500 → 100.000 total)
          --url URL                Base de la API (default http://localhost:5080)
          --output DIR             Carpeta de resultados (default results)
          --warmup N               Ventas de calentamiento antes de medir (default 0)
          --timeout S              Timeout HTTP por request en segundos (default 180)
          --sample-ms N            Intervalo de muestreo de /api/stats (default 250)
          --label TEXTO            Etiqueta de la corrida (se añade al runId y al informe)
          --inflight N             Ventas concurrentes por cajero (default 1)
          --no-csv                 No generar sales.csv (solo report.html y summary.json)
          --no-reset               No borrar la base antes de la corrida
          --open                   Abrir el report.html al terminar
          -h, --help               Esta ayuda

        La consola hace POST /api/admin/reset antes de medir (salvo --no-reset),
        lanza la carga, espera a que la cola drene, verifica la base y escribe
        results/<runId>/report.html, sales.csv y summary.json.
        """;
}
