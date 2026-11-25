using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sales.Contracts;

namespace Sales.LoadTest;

public sealed class Runner : IDisposable
{
    private const int CreatedStatus = 201;
    private const int AcceptedStatus = 202;

    private static bool IsSuccessStatus(int status)
        => status is CreatedStatus or AcceptedStatus;

    private static readonly JsonSerializerOptions ReportJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Options _options;
    private readonly ApiClient _api;

    public Runner(Options options)
    {
        _options = options;
        _api = new ApiClient(options.Url, options.TimeoutSeconds);
    }

    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.Now;
        var label = SanitizeLabel(_options.Label);
        var runId = startedAt.ToString("yyyy-MM-dd-HHmmss") + (label.Length == 0 ? "" : "-" + label);
        var outputDir = Path.GetFullPath(Path.Combine(_options.Output, runId));
        Directory.CreateDirectory(outputDir);

        await _api.WaitForHealthAsync(TimeSpan.FromSeconds(20), ct);
        Console.WriteLine($"API: {_options.Url}");

        if (_options.Reset)
        {
            var reset = await _api.ResetAsync(ct);
            Console.WriteLine($"Base reiniciada: {reset.DbPath} ({reset.CashierCount} cajeros)");
        }

        var apiInfo = await _api.GetInfoAsync(ct);
        Console.WriteLine(
            $"SQLite: journal={apiInfo.Config.JournalMode}, synchronous={apiInfo.Config.Synchronous}, " +
            $"fullfsync={(apiInfo.Config.FullFsync ? "ON" : "OFF")}");

        var catalog = await _api.GetCatalogAsync(ct);
        if (catalog.CashierIds.Count < _options.Cashiers)
        {
            throw new InvalidOperationException(
                $"La API tiene {catalog.CashierIds.Count} cajeros y se piden {_options.Cashiers}. " +
                "Ajusta Benchmark:CashierCount o --cashiers.");
        }

        if (_options.WarmupSales > 0)
        {
            var warmupCashiers = catalog.CashierIds.Take(_options.Cashiers).ToArray();
            var warmupPerCashier = Math.Max(1, _options.WarmupSales / _options.Cashiers);
            var warmupConcurrency = Math.Max(1, warmupCashiers.Length * _options.Inflight);
            Console.WriteLine($"Warm-up: {warmupPerCashier:N0} ventas × {_options.Cashiers} cajeros...");
            await RunLoadAsync(warmupCashiers, warmupPerCashier, warmupConcurrency, null, ct);
            await WaitForDrainAsync(TimeSpan.FromSeconds(60), ct);

            if (_options.Reset)
                await _api.ResetAsync(ct);

            catalog = await _api.GetCatalogAsync(ct);
        }

        var baseline = await _api.GetStatsAsync(ct);
        var cashierIds = catalog.CashierIds.Take(_options.Cashiers).ToArray();
        var perCashier = _options.SalesPerCashier;
        var attempted = cashierIds.Length * perCashier;
        var results = new SaleResult[attempted];

        using var samplerCts = new CancellationTokenSource();
        var samples = new ConcurrentQueue<TimelineSample>();
        var sampler = SampleAsync(samples, startedAt, samplerCts.Token);

        var concurrency = Math.Max(1, cashierIds.Length * _options.Inflight);
        Console.WriteLine(
            $"Lanzando {attempted:N0} ventas: {cashierIds.Length} cajeros × {perCashier:N0} " +
            $"(inflight {_options.Inflight}, concurrencia {concurrency})...");

        var runWatch = Stopwatch.StartNew();
        await RunLoadAsync(cashierIds, perCashier, concurrency, results, ct);
        runWatch.Stop();

        samplerCts.Cancel();
        try
        {
            await sampler;
        }
        catch (OperationCanceledException)
        {
        }

        var drainWatch = Stopwatch.StartNew();
        var finalStats = await WaitForDrainAsync(TimeSpan.FromSeconds(Math.Max(60, _options.TimeoutSeconds)), ct);
        drainWatch.Stop();

        var verifyWatch = Stopwatch.StartNew();
        var verify = await _api.VerifyAsync(ct);
        var persistedIds = await _api.GetPersistedIdsAsync(20_000, ct);

        var accepted = results.Count(x => IsSuccessStatus(x.HttpStatus));
        var persistedSet = persistedIds.ToHashSet();
        var missing = new List<SaleResult>();
        var rejected = new List<SaleResult>();
        var recovered = 0;

        foreach (var result in results)
        {
            var clientSaleId = ClientSaleIds.Encode(result.CashierId, result.Sequence);

            if (persistedSet.Contains(clientSaleId))
            {
                if (!IsSuccessStatus(result.HttpStatus))
                    recovered++;
            }
            else if (IsSuccessStatus(result.HttpStatus))
            {
                missing.Add(result);
            }
            else
            {
                rejected.Add(result);
            }
        }

        var expected = new ExpectedTotals(
            Count: attempted,
            Quantity: attempted, // cada request lleva quantity = 1
            TotalCents: (long)attempted * catalog.Product.UnitPriceCents);

        var perCashierOk = verify.PerCashier.Count == cashierIds.Length
            && verify.PerCashier.Select(x => x.CashierId).SequenceEqual(cashierIds)
            && verify.PerCashier.All(x =>
                x.Count == perCashier &&
                x.SumCents == (long)perCashier * catalog.Product.UnitPriceCents);

        var failures = new List<string>();
        if (accepted != attempted)
            failures.Add($"HTTP 201/202: {accepted:N0} de {attempted:N0}");
        if (verify.Count != expected.Count)
            failures.Add($"Persistidas: {verify.Count:N0} de {expected.Count:N0}");
        if (verify.DistinctIds != expected.Count)
            failures.Add($"IDs distintos: {verify.DistinctIds:N0} de {expected.Count:N0}");
        if (verify.SumQuantity != expected.Quantity)
            failures.Add($"Suma de cantidades: {verify.SumQuantity:N0} de {expected.Quantity:N0}");
        if (verify.SumTotalCents != expected.TotalCents)
            failures.Add($"Importe: {verify.SumTotalCents:N0} de {expected.TotalCents:N0} céntimos");
        if (verify.Duplicates != 0)
            failures.Add($"Duplicados: {verify.Duplicates:N0}");
        if (verify.IntegrityCheck != "ok")
            failures.Add($"integrity_check: {verify.IntegrityCheck}");
        if (missing.Count != 0)
            failures.Add($"Ventas perdidas (202 sin fila): {missing.Count:N0}");
        if (rejected.Count != 0)
            failures.Add($"Ventas rechazadas (sin 202): {rejected.Count:N0}");
        if (finalStats.Discarded != 0)
            failures.Add($"Descartadas por reset: {finalStats.Discarded:N0}");
        if (finalStats.Failed != 0)
            failures.Add($"Fallidas al guardar: {finalStats.Failed:N0}");
        if (finalStats.Persisted != verify.Count)
            failures.Add($"Contador del servidor ({finalStats.Persisted:N0}) != filas ({verify.Count:N0})");
        if (!perCashierOk)
            failures.Add($"El reparto por cajero no cuadra ({_options.SalesPerCashier:N0} cada uno / importe)");

        var pass = failures.Count == 0;

        var latencies = results.Select(x => x.LatencyMs).OrderBy(x => x).ToArray();
        var latency = new LatencyStats(
            AvgMs: latencies.Length == 0 ? 0 : latencies.Average(),
            P50Ms: Percentile(latencies, 0.50),
            P95Ms: Percentile(latencies, 0.95),
            P99Ms: Percentile(latencies, 0.99),
            MaxMs: latencies.Length == 0 ? 0 : latencies[^1]);

        var statuses = results
            .GroupBy(x => x.HttpStatus)
            .Select(g => new StatusCount(g.Key, g.Count()))
            .OrderBy(x => x.Status)
            .ToList();

        var timeline = new List<TimelineSample>
        {
            new(0, baseline.QueueLength, baseline.Persisted, baseline.Enqueued, baseline.Batches)
        };
        timeline.AddRange(samples.OrderBy(x => x.ElapsedMs));
        verifyWatch.Stop();

        var finishedAt = DateTimeOffset.Now;
        var report = new RunReport
        {
            RunId = runId,
            StartedAt = startedAt,
            FinishedAt = finishedAt,
            DurationSeconds = runWatch.Elapsed.TotalSeconds,
            DrainSeconds = drainWatch.Elapsed.TotalSeconds,
            TotalSeconds = runWatch.Elapsed.TotalSeconds + drainWatch.Elapsed.TotalSeconds,
            VerifySeconds = verifyWatch.Elapsed.TotalSeconds,
            ReportSeconds = 0,
            Pass = pass,
            Options = _options,
            Environment = new EnvironmentInfo(
                MachineName: Environment.MachineName,
                Os: RuntimeInformation.OSDescription,
                Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
                ProcessorCount: Environment.ProcessorCount,
                Runtime: Environment.Version.ToString(),
                Framework: RuntimeInformation.FrameworkDescription,
                WorkingDirectory: Directory.GetCurrentDirectory()),
            Catalog = catalog,
            ApiInfo = apiInfo,
            Baseline = baseline,
            Final = finalStats,
            Verify = verify,
            Attempted = attempted,
            Accepted = accepted,
            Persisted = (int)verify.Count,
            Lost = missing.Count,
            Rejected = rejected.Count,
            Recovered = recovered,
            Expected = expected,
            Throughput = runWatch.Elapsed.TotalSeconds <= 0 ? 0 : attempted / runWatch.Elapsed.TotalSeconds,
            Latency = latency,
            Statuses = statuses,
            Timeline = timeline,
            Histogram = BuildHistogram(latencies),
            Missing = missing.Take(100).ToList(),
            Slowest = results.OrderByDescending(x => x.LatencyMs).Take(10).ToList(),
            PerCashierOk = perCashierOk,
            Failures = failures,
            CsvFile = "sales.csv",
            JsonFile = "summary.json"
        };

        var jsonPath = Path.Combine(outputDir, report.JsonFile);

        if (_options.NoCsv)
        {
            report = report with { CsvFile = string.Empty, ReportSeconds = 0 };
        }
        else
        {
            var csvPath = Path.Combine(outputDir, report.CsvFile);
            var csvWatch = Stopwatch.StartNew();
            WriteCsv(csvPath, results);
            csvWatch.Stop();
            report = report with { ReportSeconds = csvWatch.Elapsed.TotalSeconds };
        }

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, ReportJson), ct);

        var reportPath = HtmlReport.Write(report, outputDir);

        PrintSummary(report, reportPath);

        if (_options.Open)
            TryOpen(reportPath);

        return pass ? 0 : 1;
    }

    private static string SanitizeLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return string.Empty;

        var cleaned = new string(label
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-')
            .ToArray());

        return cleaned.Trim('-');
    }

    private async Task RunLoadAsync(
        long[] cashierIds,
        int perCashier,
        int concurrency,
        SaleResult[]? results,
        CancellationToken ct)
    {
        var total = cashierIds.Length * perCashier;
        var completed = 0;
        var progressStep = Math.Max(10_000, total / 10);
        var cashierCount = cashierIds.Length;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, total),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = concurrency,
                CancellationToken = ct
            },
            async (index, token) =>
            {
                // Intercala las ventas entre cajeros para mantener "inflight" por cajero.
                var cashierId = cashierIds[index % cashierCount];
                var sequence = index / cashierCount + 1;

                var result = await SendSaleAsync(cashierId, sequence, token);

                if (results is not null)
                    results[index] = result;

                var done = Interlocked.Increment(ref completed);
                if (results is not null && done % progressStep == 0)
                    Console.WriteLine($"  {done:N0}/{total:N0} ventas");
            });
    }

    private async Task<SaleResult> SendSaleAsync(long cashierId, int sequence, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var status = 0;
        string? error = null;

        try
        {
            using var content = new StringContent(
                $"{{\"cashierId\":{cashierId},\"sequence\":{sequence},\"quantity\":1}}",
                Encoding.UTF8,
                "application/json");

            using var response = await _api.Http.PostAsync("/api/sales", content, ct);
            status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
                error = Truncate(await response.Content.ReadAsStringAsync(ct), 300);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
        }

        return new SaleResult(
            cashierId,
            sequence,
            status,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            error);
    }

    private async Task<StatsSnapshot> WaitForDrainAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var stats = await _api.GetStatsAsync(ct);

        while (DateTime.UtcNow < deadline)
        {
            if (stats.QueueLength == 0 &&
                stats.Persisted + stats.Failed + stats.Discarded >= stats.Enqueued)
            {
                return stats;
            }

            await Task.Delay(100, ct);
            stats = await _api.GetStatsAsync(ct);
        }

        return stats;
    }

    private async Task SampleAsync(
        ConcurrentQueue<TimelineSample> samples,
        DateTimeOffset start,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var stats = await _api.GetStatsAsync(ct);
                samples.Enqueue(new TimelineSample(
                    (DateTimeOffset.Now - start).TotalMilliseconds,
                    stats.QueueLength,
                    stats.Persisted,
                    stats.Enqueued,
                    stats.Batches));
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
            }

            try
            {
                await Task.Delay(_options.SampleIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static void WriteCsv(string path, SaleResult[] results)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine("cashierId,sequence,clientSaleId,httpStatus,latencyMs,error");

        foreach (var result in results)
        {
            writer.Write(result.CashierId.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(result.Sequence.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(ClientSaleIds.Encode(result.CashierId, result.Sequence).ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(result.HttpStatus.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(result.LatencyMs.ToString("F3", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(EscapeCsv(result.Error));
            writer.WriteLine();
        }
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";

        return value;
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
            return 0;

        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static IReadOnlyList<HistogramBin> BuildHistogram(double[] sorted, int buckets = 40)
    {
        if (sorted.Length == 0)
            return Array.Empty<HistogramBin>();

        var max = sorted[^1];
        var width = max <= 0 ? 1d : max / buckets;
        var counts = new int[buckets];

        foreach (var value in sorted)
        {
            var index = (int)(value / width);
            if (index < 0)
                index = 0;
            if (index >= buckets)
                index = buckets - 1;
            counts[index]++;
        }

        var bins = new HistogramBin[buckets];
        for (var i = 0; i < buckets; i++)
            bins[i] = new HistogramBin(i * width, (i + 1) * width, counts[i]);

        return bins;
    }

    private static string? Truncate(string? value, int maxLength)
        => value is null
            ? null
            : value.Length <= maxLength
                ? value
                : value[..maxLength];

    private static void PrintSummary(RunReport report, string reportPath)
    {
        Console.WriteLine();
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = report.Pass ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine(report.Pass ? "RESULTADO: PASS" : "RESULTADO: FAIL");
        Console.ForegroundColor = previous;

        Console.WriteLine($"  Intentadas        : {report.Attempted:N0}");
        Console.WriteLine($"  HTTP 201/202      : {report.Accepted:N0}");
        Console.WriteLine($"  Persistidas       : {report.Persisted:N0}");
        Console.WriteLine($"  Perdidas          : {report.Lost:N0}");
        Console.WriteLine($"  Rechazadas        : {report.Rejected:N0}");
        Console.WriteLine($"  Recuperadas       : {report.Recovered:N0}");
        Console.WriteLine($"  Duración          : {report.TotalSeconds:F2} s " +
                          $"(HTTP {report.DurationSeconds:F2} + drenaje {report.DrainSeconds:F2})");
        Console.WriteLine($"  Throughput        : {report.Throughput:N0} ventas/s");
        Console.WriteLine(
            $"  Latencia ms       : p50 {report.Latency.P50Ms:F1} | p95 {report.Latency.P95Ms:F1} | " +
            $"p99 {report.Latency.P99Ms:F1} | max {report.Latency.MaxMs:F1}");
        Console.WriteLine($"  Lotes             : {report.Final.Batches:N0} " +
                          $"(completos {report.Final.FullBatches:N0} / parciales {report.Final.PartialBatches:N0})");
        Console.WriteLine($"  SaveChanges ms    : avg {report.Final.AvgBatchSaveMs:F1} | p95 {report.Final.P95BatchSaveMs:F1}");
        Console.WriteLine($"  Verificación      : {report.VerifySeconds:F2} s (conteos + ids)");
        Console.WriteLine($"  CSV               : {report.ReportSeconds:F2} s");
        Console.WriteLine($"  Total cliente     : {report.TotalSeconds + report.VerifySeconds + report.ReportSeconds:F2} s");
        Console.WriteLine($"  Informe           : {reportPath}");

        if (!report.Pass)
        {
            Console.WriteLine("  Fallos:");
            foreach (var failure in report.Failures)
                Console.WriteLine($"   - {failure}");
        }
    }

    private static void TryOpen(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"No se pudo abrir el informe: {ex.Message}");
        }
    }

    public void Dispose() => _api.Dispose();
}
