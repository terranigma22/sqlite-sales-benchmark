using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var positional = new List<string>();
string? outputArg = null;
string? crashArg = null;
string? winnerJsonArg = null;
var noLinks = false;
var final = false;
var passes = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--no-links":
            noLinks = true;
            break;
        case "--final":
            final = true;
            noLinks = true;
            break;
        case "--passes":
            passes = true;
            final = true;
            noLinks = true;
            break;
        case "--output":
            outputArg = args[++i];
            break;
        case "--crash":
            crashArg = args[++i];
            break;
        case "--winner-json":
            winnerJsonArg = args[++i];
            break;
        default:
            positional.Add(args[i]);
            break;
    }
}

var root = positional.Count > 0 ? Path.GetFullPath(positional[0]) : Path.GetFullPath("results/matrix");

if (!Directory.Exists(root))
{
    Console.Error.WriteLine($"No existe el directorio: {root}");
    return 1;
}

var runs = new List<RunSummary>();

foreach (var file in Directory.EnumerateFiles(root, "summary.json", SearchOption.AllDirectories))
{
    try
    {
        runs.Add(Parse(file));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Ignorado {file}: {ex.Message}");
    }
}

if (runs.Count == 0)
{
    Console.Error.WriteLine($"No hay summary.json en {root}.");
    return 1;
}

var outputPath = outputArg is not null
    ? Path.GetFullPath(outputArg)
    : Path.Combine(root, "comparison.html");

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

if (passes)
{
    var passesHtml = BuildPassesHtml(runs);
    File.WriteAllText(outputPath, passesHtml, new UTF8Encoding(false));
    PrintPasses(runs, outputPath);
    return 0;
}

var groups = runs
    .GroupBy(x => x.Config)
    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
    .Select(g => new ConfigSummary(
        Config: g.Key,
        Journal: g.First().Journal,
        Synchronous: g.First().Synchronous,
        FullFsync: g.First().FullFsync,
        BatchSize: g.First().BatchSize,
        FlushMs: g.First().FlushMs,
        Inflight: g.First().Inflight,
        AckMode: g.First().AckMode,
        Reps: g.Count(),
        Pass: g.All(x => x.Pass),
        ThroughputMedian: Median(g.Select(x => x.Throughput)),
        ThroughputMin: g.Min(x => x.Throughput),
        ThroughputMax: g.Max(x => x.Throughput),
        DurationMedian: Median(g.Select(x => x.Duration)),
        TotalMedian: Median(g.Select(x => x.Total)),
        P95Median: Median(g.Select(x => x.P95)),
        P99Median: Median(g.Select(x => x.P99)),
        AvgSaveMedian: Median(g.Select(x => x.AvgSave)),
        P95SaveMedian: Median(g.Select(x => x.P95Save)),
        MaxQueue: g.Max(x => x.MaxQueue),
        DbBytes: (long)Median(g.Select(x => (double)x.DbBytes)),
        WalBytes: (long)Median(g.Select(x => (double)x.WalBytes))))
    .ToList();

var winner = groups
    .Where(x => x.Pass)
    .OrderByDescending(x => x.ThroughputMedian)
    .FirstOrDefault();

var crash = ParseCrash(crashArg);
var html = BuildHtml(root, groups, runs, winner, crash, noLinks, final);

File.WriteAllText(outputPath, html, new UTF8Encoding(false));

if (winnerJsonArg is not null)
{
    var winnerPath = Path.GetFullPath(winnerJsonArg);
    Directory.CreateDirectory(Path.GetDirectoryName(winnerPath)!);

    object? payload = winner is null
        ? null
        : new
        {
            config = winner.Config,
            journal = winner.Journal,
            synchronous = winner.Synchronous,
            fullFsync = winner.FullFsync,
            batchSize = winner.BatchSize,
            flushMs = winner.FlushMs,
            inflight = winner.Inflight,
            ackMode = winner.AckMode,
            throughput = winner.ThroughputMedian,
            p95 = winner.P95Median,
            p99 = winner.P99Median,
            saveChanges = winner.AvgSaveMedian
        };

    File.WriteAllText(
        winnerPath,
        JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
        new UTF8Encoding(false));
}

PrintTable(groups, winner);
Console.WriteLine();
Console.WriteLine($"Informe: {outputPath}");
return 0;

// ---------------------------------------------------------------------------

static RunSummary Parse(string file)
{
    using var document = JsonDocument.Parse(File.ReadAllText(file));
    var root = document.RootElement;
    var runDir = Path.GetDirectoryName(file)!;
    var parent = Directory.GetParent(runDir)?.Name ?? "unknown";
    var label = S(root, "options", "label") ?? Path.GetFileName(runDir);
    var config = parent is "matrix" or "results" ? Regex.Replace(label, @"-rep\d+$", "") : parent;

    var maxQueue = 0;
    if (root.TryGetProperty("timeline", out var timeline) && timeline.ValueKind == JsonValueKind.Array)
    {
        foreach (var sample in timeline.EnumerateArray())
        {
            if (sample.TryGetProperty("queueLength", out var queue) && queue.TryGetInt32(out var value))
                maxQueue = Math.Max(maxQueue, value);
        }
    }

    return new RunSummary(
        Config: config,
        RunId: S(root, "runId") ?? Path.GetFileName(runDir),
        Label: label,
        Journal: S(root, "apiInfo", "config", "journalMode") ?? "?",
        Synchronous: S(root, "apiInfo", "config", "synchronous") ?? "?",
        FullFsync: B(root, "apiInfo", "config", "fullFsync"),
        BatchSize: (int)L(root, "apiInfo", "writer", "batchSize"),
        FlushMs: (int)L(root, "apiInfo", "writer", "flushMs"),
        Inflight: (int)L(root, "options", "inflight"),
        AckMode: S(root, "apiInfo", "writer", "ackMode") ?? "?",
        Pass: B(root, "pass"),
        Persisted: L(root, "persisted"),
        Expected: L(root, "expected", "count"),
        Warmup: (int)L(root, "options", "warmupSales"),
        Duration: D(root, "durationSeconds"),
        Total: D(root, "totalSeconds"),
        Throughput: D(root, "throughput"),
        P50: D(root, "latency", "p50Ms"),
        P95: D(root, "latency", "p95Ms"),
        P99: D(root, "latency", "p99Ms"),
        Max: D(root, "latency", "maxMs"),
        MaxQueue: maxQueue,
        AvgSave: D(root, "final", "avgBatchSaveMs"),
        P95Save: D(root, "final", "p95BatchSaveMs"),
        DbBytes: L(root, "verify", "dbBytes"),
        WalBytes: L(root, "verify", "walBytes"),
        ReportPath: Path.Combine(runDir, "report.html"));
}

static CrashSummary? ParseCrash(string? path)
{
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        return null;

    using var document = JsonDocument.Parse(File.ReadAllText(path));
    var root = document.RootElement;
    var lost = (int)L(root, "lost");

    return new CrashSummary(
        Lost: lost,
        Accepted: (int)L(root, "accepted"),
        Persisted: (int)L(root, "persisted"),
        Rejected: (int)L(root, "rejected"),
        Recovered: (int)L(root, "recovered"),
        Expected: L(root, "expected", "count"),
        Ok: lost == 0);
}

static void PrintTable(IReadOnlyList<ConfigSummary> groups, ConfigSummary? winner)
{
    Console.WriteLine(
        $"{"Config",-30} {"Reps",4} {"PASS",5} {"Throughput med",18} {"p95",8} {"Save med",9} {"Cola",7}");
    Console.WriteLine(new string('-', 95));

    foreach (var group in groups)
    {
        var mark = winner is not null && group.Config == winner.Config ? " *" : "  ";
        var throughput = $"{N(group.ThroughputMedian)} ({N(group.ThroughputMin)}-{N(group.ThroughputMax)})";
        Console.WriteLine(
            $"{group.Config + mark,-30} {group.Reps,4} {(group.Pass ? "sí" : "NO"),5} {throughput,18} " +
            $"{group.P95Median,6:F1}ms {group.AvgSaveMedian,7:F1}ms {group.MaxQueue,7:N0}");
    }

    if (winner is not null)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"Ganadora: {winner.Config} · {winner.Journal}/{winner.Synchronous}/ff{(winner.FullFsync ? "ON" : "OFF")} · " +
            $"lote {winner.BatchSize} · ventana {winner.FlushMs} ms · inflight {winner.Inflight} · {winner.AckMode}");
    }
}

static string BuildHtml(
    string root,
    IReadOnlyList<ConfigSummary> groups,
    IReadOnlyList<RunSummary> runs,
    ConfigSummary? winner,
    CrashSummary? crash,
    bool noLinks,
    bool final)
{
    var warmup = runs.Select(x => x.Warmup).DefaultIfEmpty(0).Max();
    var reps = groups.Select(x => x.Reps).Distinct().ToList();
    var repsText = reps.Count == 1 ? $"{reps[0]} repeticiones" : "repeticiones variables";
    var total = runs.Select(x => x.Expected).DefaultIfEmpty(0).Max();
    var allPass = groups.All(x => x.Pass);
    var title = final ? "SQLite benchmark — resultado final" : "SQLite PRAGMA matrix";

    var sb = new StringBuilder();
    sb.Append($$$"""
        <!doctype html>
        <html lang="es">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{{H(title)}}}</title>
        <style>
          :root{color-scheme:dark;--bg:#0b0f14;--panel:#121820;--panel2:#0f151c;--text:#dbe4f0;--muted:#8b98ab;--line:#222c39;--accent:#4f8cff;--green:#35c28a;--red:#ff5c6c}
          *{box-sizing:border-box}
          body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 -apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,Helvetica,Arial,sans-serif}
          header{padding:24px 32px;border-bottom:1px solid var(--line);background:linear-gradient(180deg,#0f1620,#0b0f14)}
          h1{margin:0;font-size:22px;letter-spacing:-.02em}
          .subtitle{color:var(--muted);margin:4px 0 0}
          main{max-width:1400px;margin:0 auto;padding:22px 32px 56px}
          .panel{background:var(--panel);border:1px solid var(--line);border-radius:14px;padding:18px 20px;margin:18px 0}
          .panel h2{margin:0 0 10px;font-size:16px}
          .winner{border-color:rgba(53,194,138,.45);background:linear-gradient(135deg,rgba(53,194,138,.12),rgba(53,194,138,.02))}
          .winner-name{font-size:24px;font-weight:800;color:var(--green);margin:0}
          tr.winner-row td{background:rgba(53,194,138,.10)}
          .crash-status{font-size:16px;font-weight:700;margin:0 0 10px}
          .crash-status.ok{color:var(--green)}
          .crash-status.bad{color:var(--red)}
          .table-wrap{overflow-x:auto;border:1px solid var(--line);border-radius:10px;background:var(--panel2)}
          table{width:100%;border-collapse:collapse;font-variant-numeric:tabular-nums}
          th,td{padding:9px 12px;text-align:left;border-bottom:1px solid var(--line);white-space:nowrap}
          th{background:#0c1219;color:var(--muted);font-size:11px;text-transform:uppercase;letter-spacing:.06em}
          tr:last-child td{border-bottom:0}
          tbody tr:hover{background:rgba(79,140,255,.06)}
          .badge{display:inline-block;padding:2px 8px;border-radius:999px;font-size:11px;font-weight:700}
          .badge.ok{background:rgba(53,194,138,.16);color:var(--green)}
          .badge.bad{background:rgba(255,92,108,.16);color:var(--red)}
          .muted{color:var(--muted)}
          .links a{color:var(--accent);text-decoration:none;margin-right:6px}
          .links a:hover{text-decoration:underline}
          .run-links{margin:10px 0 0;padding-left:18px}
          .run-links li{margin:4px 0;color:var(--muted)}
          .run-links strong{color:var(--text)}
          svg{width:100%;height:auto;display:block}
          p{color:var(--muted)}
          @media(max-width:720px){header,main{padding:18px}}
        </style>
        </head>
        <body>
        <header>
          <h1>{{{H(title)}}}</h1>
        """);

    sb.Append("<p class=\"subtitle\">");
    if (!final)
        sb.Append(H(root)).Append(" · ");
    sb.Append($"{groups.Count} configuraciones · {runs.Count} corridas · warm-up {N(warmup)} ventas · ");
    sb.Append(allPass ? "<span style=\"color:#35c28a\">todas PASS</span>" : "<span style=\"color:#ff5c6c\">hay fallos</span>");
    sb.Append("</p></header><main>");

    if (final && winner is not null)
    {
        sb.Append("<section class=\"panel winner\"><h2>Mejor configuración</h2>");
        sb.Append($"<p class=\"winner-name\">{H(winner.Config)}</p>");
        sb.Append("<p class=\"subtitle\">");
        sb.Append(H($"{winner.Journal}/{winner.Synchronous}/ff{(winner.FullFsync ? "ON" : "OFF")}"));
        sb.Append($" · lote {winner.BatchSize} · ventana {winner.FlushMs} ms · inflight {winner.Inflight} · {H(winner.AckMode)}");
        sb.Append("</p>");
        sb.Append($"<p class=\"subtitle\">{N(winner.ThroughputMedian)}/s · p95 {F(winner.P95Median, 1)} ms · ");
        sb.Append($"p99 {F(winner.P99Median, 1)} ms · SaveChanges {F(winner.AvgSaveMedian, 1)} ms</p>");
        sb.Append("</section>");
    }

    sb.Append("<section class=\"panel\"><h2>Resultados por configuración</h2>");
    sb.Append("<div class=\"table-wrap\"><table><thead><tr>");
    foreach (var header in new[]
             {
                 "Config", "PRAGMAs", "PASS",
                 "Throughput med (min-max)", "p95 / p99", "SaveChanges"
             })
        sb.Append("<th>").Append(H(header)).Append("</th>");
    sb.Append("</tr></thead><tbody>");

    foreach (var group in groups)
    {
        var isWinner = winner is not null && group.Config == winner.Config;
        sb.Append(isWinner ? "<tr class=\"winner-row\">" : "<tr>");
        sb.Append("<td>").Append(H(ShortConfig(group.Config))).Append("</td>");
        sb.Append("<td>").Append(H(
                $"{group.Journal}/{group.Synchronous}/ff{(group.FullFsync ? "ON" : "OFF")} · " +
                $"{group.BatchSize}/{group.FlushMs}/i{group.Inflight}"))
            .Append("</td>");
        sb.Append("<td>").Append(Badge(group.Pass)).Append("</td>");
        sb.Append("<td>").Append(N(group.ThroughputMedian))
            .Append(" <span class=\"muted\">(").Append(N(group.ThroughputMin)).Append('-')
            .Append(N(group.ThroughputMax)).Append(")</span></td>");
        sb.Append("<td>").Append(F(group.P95Median, 1)).Append(" / ").Append(F(group.P99Median, 1)).Append(" ms</td>");
        sb.Append("<td>").Append(F(group.AvgSaveMedian, 1)).Append(" ms</td>");
        sb.Append("</tr>");
    }

    sb.Append("</tbody></table></div>");

    if (!noLinks)
    {
        sb.Append("<h3>Informes individuales</h3><ul class=\"run-links\">");
        foreach (var group in groups)
        {
            var links = runs
                .Where(x => x.Config == group.Config)
                .OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
                .Select(x =>
                {
                    var relative = Path.GetRelativePath(root, x.ReportPath).Replace('\\', '/');
                    return $"<a href=\"{H(relative)}\">{H(x.Label)}</a>";
                });

            sb.Append("<li><strong>").Append(H(group.Config)).Append("</strong>: ")
                .Append(string.Join(" · ", links)).Append("</li>");
        }
        sb.Append("</ul>");
    }

    sb.Append("</section>");

    if (crash is not null)
    {
        sb.Append("<section class=\"panel\"><h2>Prueba de caída (kill -9)</h2>");
        sb.Append($"<p class=\"crash-status {(crash.Ok ? "ok" : "bad")}\">");
        sb.Append(crash.Ok
            ? "PASS — ninguna venta confirmada se perdió"
            : "FAIL — hay ventas confirmadas que no están en la base");
        sb.Append("</p>");
        sb.Append("<div class=\"table-wrap\"><table><thead><tr>");
        foreach (var header in new[] { "Métrica", "Valor" })
            sb.Append("<th>").Append(H(header)).Append("</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var row in new[]
                 {
                     ("Ventas confirmadas (201/202)", N(crash.Accepted)),
                     ("Filas en la base", N(crash.Persisted)),
                     ("Perdidas", N(crash.Lost)),
                     ("Rechazadas durante la caída", N(crash.Rejected)),
                     ("Recuperadas (error HTTP pero en DB)", N(crash.Recovered)),
                     ("Ventas esperadas", N(crash.Expected))
                 })
        {
            sb.Append("<tr><td>").Append(H(row.Item1)).Append("</td><td>")
                .Append(H(row.Item2)).Append("</td></tr>");
        }
        sb.Append("</tbody></table></div>");
        sb.Append("<p>Las rechazadas son requests que fallaron mientras la API estaba caída; no son ventas confirmadas. ");
        sb.Append("La prueba exige <strong>perdidas = 0</strong>.</p>");
        sb.Append("</section>");
    }

    sb.Append("<section class=\"panel\"><h2>Throughput mediano</h2>");
    sb.Append(SvgBars(
        groups.Select(x => (x.Config, x.ThroughputMedian)).ToList(),
        "Ventas por segundo (mediana; más alto es mejor)",
        "#35c28a",
        " /s"));
    sb.Append("</section>");

    sb.Append("<section class=\"panel\"><h2>SaveChanges mediano</h2>");
    sb.Append(SvgBars(
        groups.Select(x => (x.Config, x.AvgSaveMedian)).ToList(),
        "Milisegundos por lote (mediana; más bajo es mejor)",
        "#ffb020",
        " ms"));
    sb.Append("</section>");

    sb.Append("<section class=\"panel\"><h2>Metodología</h2><p>");
    sb.Append("Cada configuración se lanzó con su propia API y variables <code>SQLITE_*</code>/<code>WRITER_*</code>. ");
    sb.Append($"Warm-up de <strong>{N(warmup)}</strong> ventas y <strong>{H(repsText)}</strong> de ");
    sb.Append($"<strong>{N(total)}</strong> ventas cada una, con base nueva por corrida. ");
    sb.Append("La tabla muestra la mediana de throughput, p95, p99 y SaveChanges. ");
    if (noLinks)
        sb.Append("No se conservaron informes individuales ni CSV; este HTML es el resultado final.");
    else
        sb.Append("Los informes individuales están enlazados en la tabla.");
    sb.Append("</p></section></main></body></html>");

    return sb.ToString();
}

static string BuildPassesHtml(IReadOnlyList<RunSummary> runs)
{
    var ordered = runs.OrderBy(x => x.RunId).ToList();
    var first = ordered[0];
    var avgTp = ordered.Average(x => x.Throughput);
    var medTp = Median(ordered.Select(x => x.Throughput));
    var minTp = ordered.Min(x => x.Throughput);
    var maxTp = ordered.Max(x => x.Throughput);
    var avgDuration = ordered.Average(x => x.Duration);
    var avgP95 = ordered.Average(x => x.P95);
    var avgP99 = ordered.Average(x => x.P99);
    var avgSave = ordered.Average(x => x.AvgSave);
    var medP95 = Median(ordered.Select(x => x.P95));
    var medP99 = Median(ordered.Select(x => x.P99));
    var medSave = Median(ordered.Select(x => x.AvgSave));
    var warmup = ordered.Max(x => x.Warmup);
    var total = ordered.Max(x => x.Expected);
    var allPass = ordered.All(x => x.Pass);

    const string css = """
        :root{color-scheme:dark;--bg:#0b0f14;--panel:#121820;--panel2:#0f151c;--text:#dbe4f0;--muted:#8b98ab;--line:#222c39;--green:#35c28a;--red:#ff5c6c;--accent:#4f8cff}
        *{box-sizing:border-box}
        body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 -apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,Helvetica,Arial,sans-serif}
        header{padding:24px 32px;border-bottom:1px solid var(--line);background:linear-gradient(180deg,#0f1620,#0b0f14)}
        h1{margin:0;font-size:22px;letter-spacing:-.02em}
        .subtitle{color:var(--muted);margin:4px 0 0}
        main{max-width:1300px;margin:0 auto;padding:22px 32px 56px}
        .panel{background:var(--panel);border:1px solid var(--line);border-radius:14px;padding:18px 20px;margin:18px 0}
        .panel h2{margin:0 0 10px;font-size:16px}
        .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px;margin:18px 0}
        .card{background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:14px 16px}
        .card .label{color:var(--muted);font-size:11px;text-transform:uppercase;letter-spacing:.08em}
        .card .value{font-size:clamp(20px,1.6vw,24px);font-weight:700;margin-top:6px;font-variant-numeric:tabular-nums;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
        .card .hint{color:var(--muted);font-size:12px;margin-top:2px}
        .card.good{border-left:3px solid var(--green)}
        .card.bad{border-left:3px solid var(--red)}
        .table-wrap{overflow-x:auto;border:1px solid var(--line);border-radius:10px;background:var(--panel2)}
        table{width:100%;border-collapse:collapse;font-variant-numeric:tabular-nums}
        th,td{padding:9px 12px;text-align:left;border-bottom:1px solid var(--line);white-space:nowrap}
        th{background:#0c1219;color:var(--muted);font-size:11px;text-transform:uppercase;letter-spacing:.06em}
        tr:last-child td{border-bottom:0}
        tbody tr:hover{background:rgba(79,140,255,.06)}
        .average-row td{background:rgba(53,194,138,.10);font-weight:600}
        .badge{display:inline-block;padding:2px 8px;border-radius:999px;font-size:11px;font-weight:700}
        .badge.ok{background:rgba(53,194,138,.16);color:var(--green)}
        .badge.bad{background:rgba(255,92,108,.16);color:var(--red)}
        .muted{color:var(--muted)}
        svg{width:100%;height:auto;display:block}
        p{color:var(--muted)}
        @media(max-width:720px){header,main{padding:18px}}
        """;

    var sb = new StringBuilder();
    sb.Append("""
        <!doctype html>
        <html lang="es">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>SQLite benchmark — pasadas</title>
        <style>
        """);
    sb.Append(css);
    sb.Append("""
        </style>
        </head>
        <body>
        <header>
          <h1>SQLite benchmark — pasadas</h1>
        """);

    sb.Append("<p class=\"subtitle\">");
    sb.Append($"{ordered.Count} pasadas · {N(total)} ventas por pasada · warm-up {N(warmup)} · ");
    sb.Append(allPass ? "<span style=\"color:#35c28a\">todas PASS</span>" : "<span style=\"color:#ff5c6c\">hay fallos</span>");
    sb.Append("</p></header><main>");

    sb.Append("<div class=\"cards\">");
    sb.Append(Card("Media", $"{N(avgTp)} /s", $"min {N(minTp)} · max {N(maxTp)}", "good"));
    sb.Append(Card("Mediana", $"{N(medTp)} /s", $"{ordered.Count} pasadas"));
    sb.Append(Card("p95 medio", $"{F(avgP95, 1)} ms", $"p99 {F(avgP99, 1)} ms"));
    sb.Append(Card("SaveChanges medio", $"{F(avgSave, 1)} ms", $"por lote de {first.BatchSize}"));
    sb.Append(Card("Ventas por pasada", N(total), $"warm-up {N(warmup)}"));
    sb.Append(Card("Estado", allPass ? "PASS" : "FAIL", allPass ? "Todas las pasadas" : "Hay fallos", allPass ? "good" : "bad"));
    sb.Append("</div>");

    sb.Append("<section class=\"panel\"><h2>Configuración</h2>");
    sb.Append("<div class=\"table-wrap\"><table><thead><tr><th>Parámetro</th><th>Valor</th></tr></thead><tbody>");
    foreach (var (key, value) in new[]
             {
                 ("journal_mode", first.Journal),
                 ("synchronous", first.Synchronous),
                 ("fullfsync", first.FullFsync ? "ON" : "OFF"),
                 ("batch_size", first.BatchSize.ToString(CultureInfo.InvariantCulture)),
                 ("flush_ms", $"{first.FlushMs} ms"),
                 ("inflight", first.Inflight.ToString(CultureInfo.InvariantCulture)),
                 ("ack_mode", first.AckMode)
             })
    {
        sb.Append("<tr><td>").Append(H(key)).Append("</td><td>").Append(H(value)).Append("</td></tr>");
    }
    sb.Append("</tbody></table></div></section>");

    sb.Append("<section class=\"panel\"><h2>Pasadas</h2><div class=\"table-wrap\"><table><thead><tr>");
    foreach (var header in new[]
             {
                 "#", "Etiqueta", "Ventas", "Throughput", "Duración", "p95", "p99", "SaveChanges", "Estado"
             })
        sb.Append("<th>").Append(H(header)).Append("</th>");
    sb.Append("</tr></thead><tbody>");

    for (var i = 0; i < ordered.Count; i++)
    {
        var run = ordered[i];
        sb.Append("<tr>");
        sb.Append("<td>").Append(i + 1).Append("</td>");
        sb.Append("<td>").Append(H(run.Label)).Append("</td>");
        sb.Append("<td>").Append(N(run.Persisted)).Append("</td>");
        sb.Append("<td>").Append(N(run.Throughput)).Append(" /s</td>");
        sb.Append("<td>").Append(F(run.Duration, 2)).Append(" s</td>");
        sb.Append("<td>").Append(F(run.P95, 1)).Append(" ms</td>");
        sb.Append("<td>").Append(F(run.P99, 1)).Append(" ms</td>");
        sb.Append("<td>").Append(F(run.AvgSave, 1)).Append(" ms</td>");
        sb.Append("<td>").Append(Badge(run.Pass)).Append("</td>");
        sb.Append("</tr>");
    }

    sb.Append("<tr class=\"average-row\"><td>Media</td><td>—</td><td>").Append(N(total))
        .Append("</td><td>").Append(N(avgTp)).Append(" /s</td><td>").Append(F(avgDuration, 2))
        .Append(" s</td><td>").Append(F(avgP95, 1)).Append(" ms</td><td>").Append(F(avgP99, 1))
        .Append(" ms</td><td>").Append(F(avgSave, 1)).Append(" ms</td><td>").Append(Badge(allPass))
        .Append("</td></tr>");

    sb.Append("<tr class=\"average-row\"><td>Mediana</td><td>—</td><td>").Append(N(total))
        .Append("</td><td>").Append(N(medTp)).Append(" /s</td><td>—</td><td>").Append(F(medP95, 1))
        .Append(" ms</td><td>").Append(F(medP99, 1)).Append(" ms</td><td>").Append(F(medSave, 1))
        .Append(" ms</td><td>").Append(Badge(allPass)).Append("</td></tr>");

    sb.Append("</tbody></table></div></section>");

    sb.Append("<section class=\"panel\"><h2>Throughput por pasada</h2>");
    sb.Append(SvgBars(
        ordered.Select((run, index) => ($"P{index + 1}", run.Throughput)).ToList(),
        "Ventas por segundo (la línea discontinua es la media)",
        "#35c28a",
        " /s",
        avgTp));
    sb.Append("</section>");

    sb.Append("<section class=\"panel\"><h2>Metodología</h2><p>");
    sb.Append($"{ordered.Count} pasadas de {N(total)} ventas cada una, con la misma configuración. ");
    sb.Append($"Warm-up de {N(warmup)} ventas antes de cada pasada; cada pasada resetea la base. ");
    sb.Append("La API se mantiene caliente entre pasadas. La media es aritmética; la mediana se incluye para ver la dispersión. ");
    sb.Append("No se generaron CSV ni informes individuales.");
    sb.Append("</p></section></main></body></html>");

    return sb.ToString();
}

static void PrintPasses(IReadOnlyList<RunSummary> runs, string outputPath)
{
    var ordered = runs.OrderBy(x => x.RunId).ToList();
    var avg = ordered.Average(x => x.Throughput);
    var med = Median(ordered.Select(x => x.Throughput));

    Console.WriteLine($"{"Pasada",-8} {"Throughput",14} {"Duración",10} {"p95",8} {"p99",8} {"Save",9} {"Estado",7}");
    Console.WriteLine(new string('-', 70));

    for (var i = 0; i < ordered.Count; i++)
    {
        var run = ordered[i];
        Console.WriteLine(
            $"{"P" + (i + 1),-8} {N(run.Throughput),14} {F(run.Duration, 2) + "s",10} " +
            $"{F(run.P95, 1) + "ms",8} {F(run.P99, 1) + "ms",8} {F(run.AvgSave, 1) + "ms",9} " +
            $"{(run.Pass ? "PASS" : "FAIL"),7}");
    }

    Console.WriteLine(new string('-', 70));
    Console.WriteLine($"{"MEDIA",-8} {N(avg),14}");
    Console.WriteLine($"{"MEDIANA",-8} {N(med),14}");
    Console.WriteLine();
    Console.WriteLine($"Informe: {outputPath}");
}

static string Card(string label, string value, string hint, string css = "")
    => $"<div class=\"card {css}\"><div class=\"label\">{H(label)}</div>" +
       $"<div class=\"value\">{H(value)}</div><div class=\"hint\">{H(hint)}</div></div>";

static string SvgBars(
    IReadOnlyList<(string Label, double Value)> values,
    string title,
    string color,
    string unit,
    double? average = null)
{
    const int width = 1000;
    const int height = 300;
    const int padLeft = 90;
    const int padRight = 20;
    const int padTop = 50;
    const int padBottom = 60;

    if (values.Count == 0)
        return string.Empty;

    var innerWidth = width - padLeft - padRight;
    var innerHeight = height - padTop - padBottom;
    var max = Math.Max(1, values.Max(x => x.Value));
    var barWidth = (double)innerWidth / values.Count;

    var sb = new StringBuilder();
    sb.Append($"<svg viewBox=\"0 0 {width} {height}\" role=\"img\" aria-label=\"{H(title)}\">");
    sb.Append($"<text x=\"{padLeft}\" y=\"24\" font-size=\"14\" fill=\"#dbe4f0\">{H(title)}</text>");

    for (var i = 0; i <= 4; i++)
    {
        var y = padTop + innerHeight * i / 4.0;
        var value = max * (4 - i) / 4.0;
        sb.Append($"<line x1=\"{padLeft}\" y1=\"{y:F1}\" x2=\"{width - padRight}\" y2=\"{y:F1}\" stroke=\"#222c39\" />");
        sb.Append($"<text x=\"8\" y=\"{y + 4:F1}\" font-size=\"11\" fill=\"#8b98ab\">{N(value)}</text>");
    }

    for (var i = 0; i < values.Count; i++)
    {
        var barHeight = innerHeight * values[i].Value / max;
        var x = padLeft + i * barWidth + barWidth * 0.15;
        var y = padTop + innerHeight - barHeight;
        var center = padLeft + i * barWidth + barWidth / 2;

        sb.Append($"<rect x=\"{x:F1}\" y=\"{y:F1}\" width=\"{barWidth * 0.7:F1}\" height=\"{barHeight:F1}\" fill=\"{color}\" rx=\"3\" />");
        sb.Append($"<text x=\"{center:F1}\" y=\"{y - 6:F1}\" font-size=\"11\" fill=\"#dbe4f0\" text-anchor=\"middle\">{N(values[i].Value)}{H(unit)}</text>");
        sb.Append($"<text x=\"{center:F1}\" y=\"{height - padBottom + 20}\" font-size=\"11\" fill=\"#8b98ab\" text-anchor=\"middle\">{H(values[i].Label)}</text>");
    }

    if (average.HasValue)
    {
        var averageY = padTop + innerHeight - innerHeight * average.Value / max;
        sb.Append($"<line x1=\"{padLeft}\" y1=\"{averageY:F1}\" x2=\"{width - padRight}\" y2=\"{averageY:F1}\" stroke=\"#ff5c6c\" stroke-width=\"1.5\" stroke-dasharray=\"6 4\" />");
        sb.Append($"<text x=\"{width - padRight - 8}\" y=\"{averageY - 6:F1}\" font-size=\"11\" fill=\"#ff5c6c\" text-anchor=\"end\">media {N(average.Value)}{H(unit)}</text>");
    }

    sb.Append("</svg>");
    return sb.ToString();
}

static string ShortConfig(string config)
    => Regex.Replace(config, @"-b\d+-f\d+-i\d+$", "");

static string Badge(bool pass)
    => pass
        ? "<span class=\"badge ok\">PASS</span>"
        : "<span class=\"badge bad\">FAIL</span>";

static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

static string N(double value) => value.ToString("N0", CultureInfo.InvariantCulture);

static string F(double value, int decimals)
    => value.ToString("F" + decimals, CultureInfo.InvariantCulture);

static double Median(IEnumerable<double> source)
{
    var values = source.OrderBy(x => x).ToArray();
    if (values.Length == 0)
        return 0;

    var middle = values.Length / 2;
    return values.Length % 2 == 0
        ? (values[middle - 1] + values[middle]) / 2
        : values[middle];
}

static JsonElement? Get(JsonElement root, params string[] path)
{
    var current = root;

    foreach (var key in path)
    {
        if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out var next))
            return null;
        current = next;
    }

    return current;
}

static string? S(JsonElement root, params string[] path)
{
    var element = Get(root, path);
    return element.HasValue && element.Value.ValueKind == JsonValueKind.String
        ? element.Value.GetString()
        : null;
}

static bool B(JsonElement root, params string[] path)
{
    var element = Get(root, path);
    return element.HasValue
           && element.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
           && element.Value.GetBoolean();
}

static double D(JsonElement root, params string[] path)
{
    var element = Get(root, path);
    return element.HasValue && element.Value.ValueKind == JsonValueKind.Number
        ? element.Value.GetDouble()
        : 0;
}

static long L(JsonElement root, params string[] path)
{
    var element = Get(root, path);
    return element.HasValue && element.Value.ValueKind == JsonValueKind.Number
        ? element.Value.GetInt64()
        : 0;
}

internal sealed record RunSummary(
    string Config,
    string RunId,
    string Label,
    string Journal,
    string Synchronous,
    bool FullFsync,
    int BatchSize,
    int FlushMs,
    int Inflight,
    string AckMode,
    bool Pass,
    long Persisted,
    long Expected,
    int Warmup,
    double Duration,
    double Total,
    double Throughput,
    double P50,
    double P95,
    double P99,
    double Max,
    int MaxQueue,
    double AvgSave,
    double P95Save,
    long DbBytes,
    long WalBytes,
    string ReportPath);

internal sealed record ConfigSummary(
    string Config,
    string Journal,
    string Synchronous,
    bool FullFsync,
    int BatchSize,
    int FlushMs,
    int Inflight,
    string AckMode,
    int Reps,
    bool Pass,
    double ThroughputMedian,
    double ThroughputMin,
    double ThroughputMax,
    double DurationMedian,
    double TotalMedian,
    double P95Median,
    double P99Median,
    double AvgSaveMedian,
    double P95SaveMedian,
    int MaxQueue,
    long DbBytes,
    long WalBytes);

internal sealed record CrashSummary(
    int Lost,
    int Accepted,
    int Persisted,
    int Rejected,
    int Recovered,
    long Expected,
    bool Ok);
