using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sales.Contracts;

namespace Sales.LoadTest;

public static class HtmlReport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Write(RunReport report, string outputDir)
    {
        var template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "report.template.html"));
        var json = JsonSerializer.Serialize(report, JsonOptions).Replace("<", "\\u003c", StringComparison.Ordinal);

        var banner = $"{N(report.Persisted)} / {N(report.Expected.Count)} ventas persistidas · " +
                     $"{N(report.Lost)} perdidas · {N((long)Math.Round(report.Throughput))} ventas/s · " +
                     $"{report.TotalSeconds:F2} s";

        var html = template
            .Replace("{{TITLE}}", H($"SQLite Sales Benchmark — {report.RunId}"), StringComparison.Ordinal)
            .Replace("{{RUN_ID}}", H(report.RunId), StringComparison.Ordinal)
            .Replace("{{STATUS_CLASS}}", report.Pass ? "pass" : "fail", StringComparison.Ordinal)
            .Replace("{{STATUS_TEXT}}", report.Pass ? "PASS" : "FAIL", StringComparison.Ordinal)
            .Replace("{{BANNER_SUB}}", H(banner), StringComparison.Ordinal)
            .Replace("{{GENERATED_AT}}", H(report.FinishedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)), StringComparison.Ordinal)
            .Replace("{{SUMMARY_CARDS}}", BuildSummaryCards(report), StringComparison.Ordinal)
            .Replace("{{CONFIG}}", BuildConfig(report), StringComparison.Ordinal)
            .Replace("{{VERIFICATION}}", BuildVerification(report), StringComparison.Ordinal)
            .Replace("{{TIMING}}", BuildTiming(report), StringComparison.Ordinal)
            .Replace("{{CHARTS}}", BuildCharts(report), StringComparison.Ordinal)
            .Replace("{{TABLES}}", BuildTables(report), StringComparison.Ordinal)
            .Replace("{{FAILURES}}", BuildFailures(report), StringComparison.Ordinal)
            .Replace("{{DATA_JSON}}", json, StringComparison.Ordinal);

        var path = Path.Combine(outputDir, "report.html");
        File.WriteAllText(path, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string BuildSummaryCards(RunReport report)
    {
        var cards = new StringBuilder();

        cards.Append(Card(
            "Estado",
            report.Pass ? "PASS" : "FAIL",
            report.Pass ? "Todas las comprobaciones" : $"{report.Failures.Count} fallo(s)",
            report.Pass ? "good" : "bad"));

        cards.Append(Card(
            "Persistidas",
            N(report.Persisted),
            $"de {N(report.Expected.Count)} · filas en Sales",
            report.Persisted == report.Expected.Count ? "good" : "bad"));

        cards.Append(Card(
            "Perdidas",
            N(report.Lost),
            "202 sin fila en la DB",
            report.Lost == 0 ? "good" : "bad"));

        cards.Append(Card(
            "Throughput",
            $"{N((long)Math.Round(report.Throughput))} /s",
            $"{report.DurationSeconds:F2} s HTTP + {report.DrainSeconds:F2} s drenaje"));

        cards.Append(Card(
            "Latencia p95",
            $"{report.Latency.P95Ms:F1} ms",
            $"p99 {report.Latency.P99Ms:F1} ms · max {report.Latency.MaxMs:F1} ms"));

        cards.Append(Card(
            "Lotes",
            N(report.Final.Batches),
            $"{N(report.Final.FullBatches)} completos · {N(report.Final.PartialBatches)} parciales"));

        return $"<div class=\"cards\">{cards}</div>";
    }

    private static string BuildConfig(RunReport report)
    {
        var env = report.Environment;
        var config = report.ApiInfo;

        var environmentRows = new List<IReadOnlyList<string>>
        {
            Row("Máquina", H(env.MachineName)),
            Row("SO", H(env.Os)),
            Row("CPU", H($"{env.ProcessorCount} núcleos · {env.Architecture}")),
            Row(".NET", H(env.Framework)),
            Row("Directorio", H(env.WorkingDirectory)),
            Row("Base de datos", H(config.DbPath)),
            Row("Inflight por cajero", N(report.Options.Inflight))
        };

        var sqliteRows = new List<IReadOnlyList<string>>
        {
            Row("journal_mode", H(config.Config.JournalMode)),
            Row("synchronous", H(config.Config.Synchronous)),
            Row("fullfsync", config.Config.FullFsync ? "ON" : "OFF"),
            Row("checkpoint_fullfsync", config.Config.CheckpointFullFsync ? "ON" : "OFF"),
            Row("cache_size", $"{N(config.Config.CacheSizeKb)} KB"),
            Row("busy_timeout", $"{N(config.Config.BusyTimeoutMs)} ms"),
            Row("batch_size", N(config.Writer.BatchSize)),
            Row("flush_ms", $"{N(config.Writer.FlushMs)} ms"),
            Row("ack_mode", H(config.Writer.AckMode))
        };

        foreach (var pragma in config.Pragmas.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            sqliteRows.Add(Row(H(pragma.Key), H(pragma.Value)));

        return Section(
            "Configuración",
            $"<div class=\"grid2\">{Table(new[] { "Entorno y configuración", "Valor" }, environmentRows)}" +
            $"{Table(new[] { "SQLite / writer", "Valor efectivo" }, sqliteRows)}</div>",
            "Se lee de /api/info al inicio de la corrida.");
    }

    private static string BuildVerification(RunReport report)
    {
        var diff = report.Verify.SumTotalCents - report.Expected.TotalCents;

        var checks = new List<IReadOnlyList<string>>
        {
            Row("Conteo de ventas", N(report.Expected.Count), N(report.Verify.Count),
                Status(report.Verify.Count == report.Expected.Count)),
            Row("IDs distintos", N(report.Expected.Count), N(report.Verify.DistinctIds),
                Status(report.Verify.DistinctIds == report.Expected.Count)),
            Row("Suma de cantidades", N(report.Expected.Quantity), N(report.Verify.SumQuantity),
                Status(report.Verify.SumQuantity == report.Expected.Quantity)),
            Row("Importe total", Money(report.Expected.TotalCents), Money(report.Verify.SumTotalCents),
                Status(report.Verify.SumTotalCents == report.Expected.TotalCents)),
            Row("Diferencia de importe", "—", Money(diff), Status(diff == 0)),
            Row("Duplicados", "0", N(report.Verify.Duplicates), Status(report.Verify.Duplicates == 0)),
            Row("integrity_check", "ok", H(report.Verify.IntegrityCheck), Status(report.Verify.IntegrityCheck == "ok")),
            Row("Reparto por cajero", $"{N(report.Options.SalesPerCashier)} × {N(report.Options.Cashiers)}",
                report.PerCashierOk ? "OK" : "NO", Status(report.PerCashierOk)),
            Row("Base de datos / WAL", "—", $"{Bytes(report.Verify.DbBytes)} / {Bytes(report.Verify.WalBytes)}", "")
        };

        var flow = new List<IReadOnlyList<string>>
        {
            Row("Intentadas", N(report.Attempted)),
            Row("HTTP 202", N(report.Accepted)),
            Row("Persistidas", N(report.Persisted)),
            Row("Perdidas", N(report.Lost)),
            Row("Rechazadas", N(report.Rejected)),
            Row("Recuperadas (error HTTP pero en DB)", N(report.Recovered))
        };

        var checksTable = Table(new[] { "Comprobación", "Esperado", "Real", "Estado" }, checks);
        var flowTable = Table(new[] { "Flujo", "Valor" }, flow);

        return Section(
            "Cuadre y verificación",
            $"<div class=\"grid2\">{checksTable}{flowTable}</div>",
            "Lo esperado lo calcula el cliente con lo que envió; lo real sale de SQLite.");
    }

    private static string BuildTiming(RunReport report)
    {
        var serverWindow = "—";
        if (DateTimeOffset.TryParse(report.Final.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var firstEnqueue) &&
            DateTimeOffset.TryParse(report.Final.LastPersistedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastCommit))
        {
            serverWindow = $"{(lastCommit - firstEnqueue).TotalSeconds:F2} s (primer enqueue → último commit)";
        }

        var globalThroughput = report.Attempted / Math.Max(report.TotalSeconds, 0.001);

        var rows = new List<IReadOnlyList<string>>
        {
            Row("Fase HTTP (100.000 requests)", $"{report.DurationSeconds:F2} s"),
            Row("Drenaje de la cola", $"{report.DrainSeconds:F2} s"),
            Row("Duración total", $"{report.TotalSeconds:F2} s"),
            Row("Throughput HTTP", $"{N((long)Math.Round(report.Throughput))} ventas/s"),
            Row("Throughput global (incluye drenaje)", $"{N((long)Math.Round(globalThroughput))} ventas/s"),
            Row("Procesamiento del servidor", serverWindow),
            Row("Latencia media", $"{report.Latency.AvgMs:F2} ms"),
            Row("Latencia p50", $"{report.Latency.P50Ms:F2} ms"),
            Row("Latencia p95", $"{report.Latency.P95Ms:F2} ms"),
            Row("Latencia p99", $"{report.Latency.P99Ms:F2} ms"),
            Row("Latencia máxima", $"{report.Latency.MaxMs:F2} ms"),
            Row("Lotes", N(report.Final.Batches)),
            Row("Lotes completos / parciales", $"{N(report.Final.FullBatches)} / {N(report.Final.PartialBatches)}"),
            Row("Tamaño medio de lote", report.Final.AvgBatchSize.ToString("F1", CultureInfo.InvariantCulture)),
            Row("SaveChanges medio / p95", $"{report.Final.AvgBatchSaveMs:F2} / {report.Final.P95BatchSaveMs:F2} ms"),
            Row("Espera en cola media / máxima", $"{report.Final.AvgEnqueueWaitMs:F2} / {report.Final.MaxEnqueueWaitMs:F2} ms"),
            Row("Verificación (conteos + ids)", $"{report.VerifySeconds:F2} s"),
            Row("CSV de ventas", $"{report.ReportSeconds:F2} s"),
            Row("Total cliente (HTTP + drenaje + verificación + CSV)",
                $"{report.TotalSeconds + report.VerifySeconds + report.ReportSeconds:F2} s"),
            Row("Base de datos / WAL", $"{Bytes(report.Verify.DbBytes)} / {Bytes(report.Verify.WalBytes)}")
        };

        return Section(
            "Tiempos",
            Table(new[] { "Métrica", "Valor" }, rows),
            "La latencia por request es hasta el 202 (encolado); el coste de commit se mide por lote en SaveChanges.");
    }

    private static string BuildCharts(RunReport report)
    {
        var charts =
            "<div class=\"charts\">" +
            $"<div class=\"chart\"><h3>Distribución de latencia (ms)</h3>{LatencySvg(report.Histogram)}</div>" +
            $"<div class=\"chart\"><h3>Cola del Channel</h3>{LineSvg(QueuePoints(report.Timeline), "#ffb020", "cola")}</div>" +
            $"<div class=\"chart\"><h3>Throughput instantáneo (ventas/s)</h3>{LineSvg(ThroughputPoints(report.Timeline), "#35c28a", "ventas/s")}</div>" +
            "</div>";

        return Section(
            "Gráficas",
            charts,
            "Muestreo de /api/stats cada 250 ms; las latencias salen de cada request del cliente.");
    }

    private static List<(double X, double Y)> QueuePoints(IReadOnlyList<TimelineSample> timeline)
        => timeline.Select(t => (t.ElapsedMs, (double)t.QueueLength)).ToList();

    private static List<(double X, double Y)> ThroughputPoints(IReadOnlyList<TimelineSample> timeline)
    {
        var points = new List<(double, double)>();

        for (var i = 1; i < timeline.Count; i++)
        {
            var seconds = (timeline[i].ElapsedMs - timeline[i - 1].ElapsedMs) / 1000d;
            var delta = timeline[i].Persisted - timeline[i - 1].Persisted;

            if (seconds > 0)
                points.Add((timeline[i].ElapsedMs, Math.Max(0, delta / seconds)));
        }

        return points;
    }

    private static string LatencySvg(IReadOnlyList<HistogramBin> bins)
    {
        const int width = 800;
        const int height = 240;
        if (bins.Count == 0)
            return EmptySvg(width, height);

        const int padLeft = 52;
        const int padRight = 12;
        const int padTop = 14;
        const int padBottom = 46;
        var innerWidth = width - padLeft - padRight;
        var innerHeight = height - padTop - padBottom;
        var max = Math.Max(1, bins.Max(x => x.Count));

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg viewBox=\"0 0 {width} {height}\" role=\"img\" aria-label=\"Distribución de latencia\">");

        for (var i = 0; i <= 4; i++)
        {
            var y = padTop + innerHeight * i / 4.0;
            var value = (long)Math.Round(max * (4 - i) / 4.0);
            svg.Append(CultureInfo.InvariantCulture, $"<line x1=\"{padLeft}\" y1=\"{y:F1}\" x2=\"{width - padRight}\" y2=\"{y:F1}\" stroke=\"#222c39\" />");
            svg.Append(CultureInfo.InvariantCulture, $"<text x=\"4\" y=\"{y + 4:F1}\" font-size=\"11\" fill=\"#8b98ab\">{value}</text>");
        }

        var barWidth = (double)innerWidth / bins.Count;
        for (var i = 0; i < bins.Count; i++)
        {
            var barHeight = innerHeight * bins[i].Count / max;
            var x = padLeft + i * barWidth + 0.75;
            var y = padTop + innerHeight - barHeight;
            svg.Append(CultureInfo.InvariantCulture,
                $"<rect x=\"{x:F1}\" y=\"{y:F1}\" width=\"{Math.Max(1, barWidth - 1.5):F1}\" height=\"{barHeight:F1}\" fill=\"#4f8cff\" />");
        }

        var step = Math.Max(1, (int)Math.Ceiling(bins.Count / 8.0));
        for (var i = 0; i < bins.Count; i += step)
        {
            var x = padLeft + i * barWidth + barWidth / 2;
            svg.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{x:F1}\" y=\"{height - padBottom + 16}\" font-size=\"11\" fill=\"#8b98ab\" text-anchor=\"middle\">{bins[i].StartMs:F1}</text>");
        }

        svg.Append("</svg>");
        return svg.ToString();
    }

    private static string LineSvg(IReadOnlyList<(double X, double Y)> points, string color, string label)
    {
        const int width = 800;
        const int height = 240;
        if (points.Count == 0)
            return EmptySvg(width, height);

        const int padLeft = 60;
        const int padRight = 14;
        const int padTop = 18;
        const int padBottom = 30;
        var innerWidth = width - padLeft - padRight;
        var innerHeight = height - padTop - padBottom;
        var maxX = Math.Max(1, points.Max(p => p.X));
        var maxY = Math.Max(1, points.Max(p => p.Y));

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg viewBox=\"0 0 {width} {height}\" role=\"img\" aria-label=\"{H(label)}\">");

        for (var i = 0; i <= 4; i++)
        {
            var y = padTop + innerHeight * i / 4.0;
            var value = (long)Math.Round(maxY * (4 - i) / 4.0);
            svg.Append(CultureInfo.InvariantCulture, $"<line x1=\"{padLeft}\" y1=\"{y:F1}\" x2=\"{width - padRight}\" y2=\"{y:F1}\" stroke=\"#222c39\" />");
            svg.Append(CultureInfo.InvariantCulture, $"<text x=\"4\" y=\"{y + 4:F1}\" font-size=\"11\" fill=\"#8b98ab\">{value:N0}</text>");
        }

        var coordinates = new StringBuilder();
        foreach (var point in points)
        {
            var x = padLeft + innerWidth * point.X / maxX;
            var y = padTop + innerHeight - innerHeight * point.Y / maxY;
            if (coordinates.Length > 0)
                coordinates.Append(' ');
            coordinates.Append(CultureInfo.InvariantCulture, $"{x:F1},{y:F1}");
        }

        svg.Append(CultureInfo.InvariantCulture,
            $"<polyline points=\"{coordinates}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"2\" />");
        svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{padLeft}\" y=\"{height - 8}\" font-size=\"11\" fill=\"#8b98ab\">0 s</text>");
        svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{width - padRight - 34}\" y=\"{height - 8}\" font-size=\"11\" fill=\"#8b98ab\">{maxX / 1000:F1} s</text>");
        svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{padLeft + 4}\" y=\"{padTop - 12}\" width=\"10\" height=\"4\" fill=\"{color}\" />");
        svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{padLeft + 20}\" y=\"{padTop - 6}\" font-size=\"11\" fill=\"#c7d2e0\">{H(label)}</text>");
        svg.Append("</svg>");
        return svg.ToString();
    }

    private static string EmptySvg(int width, int height)
        => $"<svg viewBox=\"0 0 {width} {height}\" role=\"img\">" +
           $"<text x=\"{width / 2}\" y=\"{height / 2}\" text-anchor=\"middle\" font-size=\"13\" fill=\"#8b98ab\">Sin datos</text></svg>";

    private static string BuildTables(RunReport report)
    {
        var price = report.Catalog.Product.UnitPriceCents;

        var perCashierRows = report.Verify.PerCashier
            .Select(x =>
            {
                var ok = x.Count == report.Options.SalesPerCashier &&
                         x.SumCents == (long)report.Options.SalesPerCashier * price;
                return Row(N(x.CashierId), N(x.Count), Money(x.SumCents), Status(ok));
            })
            .ToList();

        var perCashier =
            $"<details><summary>Detalle por cajero ({N(report.Verify.PerCashier.Count)})</summary>" +
            Table(new[] { "Cajero", "Ventas", "Importe", "Estado" }, perCashierRows) +
            "</details>";

        var statusRows = report.Statuses
            .Select(x => Row(N(x.Status), N(x.Count), H(StatusLabel(x.Status))))
            .ToList();

        var statusTable = "<h3>Respuestas HTTP</h3>" +
                          Table(new[] { "HTTP", "Cantidad", "Significado" }, statusRows);

        var lostTable = report.Lost == 0
            ? "<h3>Ventas perdidas</h3><p class=\"ok-text\">Ninguna: todas las ventas aceptadas están en la base.</p>"
            : "<h3>Ventas perdidas</h3>" +
              Table(
                  new[] { "Cajero", "Secuencia", "ClientSaleId", "HTTP", "Latencia (ms)", "Error" },
                  report.Missing.Select(x => Row(
                      N(x.CashierId),
                      N(x.Sequence),
                      N(ClientSaleIds.Encode(x.CashierId, x.Sequence)),
                      N(x.HttpStatus),
                      x.LatencyMs.ToString("F2", CultureInfo.InvariantCulture),
                      H(x.Error ?? string.Empty))).ToList()) +
              (report.Lost > report.Missing.Count
                  ? $"<p class=\"subtitle\">Mostrando {N(report.Missing.Count)} de {N(report.Lost)}.</p>"
                  : string.Empty);

        var slowestTable = report.Slowest.Count == 0
            ? "<h3>10 requests más lentos</h3><p class=\"subtitle\">Sin datos.</p>"
            : "<h3>10 requests más lentos</h3>" +
              Table(
                  new[] { "Cajero", "Secuencia", "HTTP", "Latencia (ms)", "Error" },
                  report.Slowest.Select(x => Row(
                      N(x.CashierId),
                      N(x.Sequence),
                      N(x.HttpStatus),
                      x.LatencyMs.ToString("F2", CultureInfo.InvariantCulture),
                      H(x.Error ?? string.Empty))).ToList());

        return Section("Detalle", perCashier + statusTable + lostTable + slowestTable);
    }

    private static string BuildFailures(RunReport report)
    {
        if (report.Failures.Count == 0)
            return Section("Fallos", "<p class=\"ok-text\">Ninguno: todas las comprobaciones pasaron.</p>");

        var items = string.Concat(report.Failures.Select(f => $"<li>{H(f)}</li>"));
        return Section("Fallos", $"<ul class=\"failures\">{items}</ul>");
    }

    private static string Section(string title, string content, string? subtitle = null)
    {
        var sub = subtitle is null ? string.Empty : $"<p class=\"subtitle\">{H(subtitle)}</p>";
        return $"<section class=\"panel\"><h2>{H(title)}</h2>{sub}{content}</section>";
    }

    private static string Card(string label, string value, string hint, string css = "")
        => $"<div class=\"card {css}\"><div class=\"label\">{H(label)}</div>" +
           $"<div class=\"value\">{H(value)}</div><div class=\"hint\">{H(hint)}</div></div>";

    private static string Status(bool ok, string okText = "OK", string badText = "FALLA")
        => $"<span class=\"badge {(ok ? "ok" : "bad")}\">{(ok ? H(okText) : H(badText))}</span>";

    private static string Table(
        IReadOnlyList<string> headers,
        IEnumerable<IReadOnlyList<string>> rows,
        string? css = null)
    {
        var sb = new StringBuilder();
        sb.Append("<div class=\"table-wrap\"><table");
        if (!string.IsNullOrEmpty(css))
            sb.Append(" class=\"").Append(H(css)).Append('"');
        sb.Append("><thead><tr>");

        foreach (var header in headers)
            sb.Append("<th>").Append(H(header)).Append("</th>");

        sb.Append("</tr></thead><tbody>");

        foreach (var row in rows)
        {
            sb.Append("<tr>");
            foreach (var cell in row)
                sb.Append("<td>").Append(cell).Append("</td>");
            sb.Append("</tr>");
        }

        sb.Append("</tbody></table></div>");
        return sb.ToString();
    }

    private static string[] Row(params string[] cells) => cells;

    private static string StatusLabel(int status) => status switch
    {
        201 => "Created (commit durable)",
        202 => "Accepted (encolada)",
        200 => "OK",
        400 => "Bad Request (validación)",
        499 => "Client Closed Request",
        503 => "Service Unavailable (reset)",
        0 => "Sin respuesta (timeout / error de red)",
        _ => $"HTTP {status}"
    };

    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Money(long cents)
    {
        var sign = cents < 0 ? "-" : string.Empty;
        var absolute = Math.Abs(cents);
        return sign + "$" + (absolute / 100m).ToString("N2", CultureInfo.InvariantCulture);
    }

    private static string Bytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return (bytes / 1024d).ToString("F1", CultureInfo.InvariantCulture) + " KB";
        if (bytes < 1024L * 1024 * 1024)
            return (bytes / 1024d / 1024).ToString("F2", CultureInfo.InvariantCulture) + " MB";
        return (bytes / 1024d / 1024 / 1024).ToString("F2", CultureInfo.InvariantCulture) + " GB";
    }

    private static string H(string value) => WebUtility.HtmlEncode(value);
}
