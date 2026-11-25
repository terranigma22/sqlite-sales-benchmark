using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Sales.Api.Data;
using Sales.Api.Metrics;
using Sales.Api.Queue;
using Sales.Api.Services;
using Sales.Contracts;

var builder = WebApplication.CreateBuilder(args);

var dbPath = builder.Configuration["SALES_DB"]
             ?? builder.Configuration["Database:Path"]
             ?? Path.Combine(AppContext.BaseDirectory, "data", "sales.db");

var paths = new DatabasePaths(dbPath);
Directory.CreateDirectory(Path.GetDirectoryName(paths.DbPath)!);

var sqliteSettings = SqliteSettings.FromConfiguration(builder.Configuration);
var writerSettings = WriterSettings.FromConfiguration(builder.Configuration);

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(sqliteSettings);
builder.Services.AddSingleton(writerSettings);
builder.Services.AddSingleton<SqlitePragmaInterceptor>();
builder.Services.AddDbContextFactory<AppDbContext>((sp, options) =>
{
    options.UseSqlite(sp.GetRequiredService<DatabasePaths>().ConnectionString);
    options.AddInterceptors(sp.GetRequiredService<SqlitePragmaInterceptor>());
});

builder.Services.AddSingleton<SaleQueue>();
builder.Services.AddSingleton<SalesMetrics>();
builder.Services.AddSingleton<WriterMutex>();
builder.Services.AddSingleton<WriterGate>();
builder.Services.AddSingleton<CatalogCache>();
builder.Services.AddSingleton<ResetService>();
builder.Services.AddSingleton<VerifyService>();
builder.Services.AddHostedService<SalesWriterService>();

var app = builder.Build();

await app.Services.GetRequiredService<ResetService>().InitializeAsync(CancellationToken.None);

app.MapGet("/", (DatabasePaths p) => Results.Ok(new
{
    service = "SQLite Sales Benchmark API",
    db = p.DbPath,
    endpoints = new[]
    {
        "GET  /api/health",
        "GET  /api/info",
        "GET  /api/catalog",
        "GET  /api/stats",
        "POST /api/sales",
        "POST /api/admin/reset",
        "GET  /api/admin/verify",
        "GET  /api/admin/persisted-ids?after=0&limit=20000"
    }
}));

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow }));

app.MapGet("/api/info", async (
    DatabasePaths p,
    SqliteSettings settings,
    WriterSettings writer,
    IDbContextFactory<AppDbContext> factory,
    CancellationToken ct) =>
{
    var pragmas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    await using var db = await factory.CreateDbContextAsync(ct);
    var connection = db.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open)
        await connection.OpenAsync(ct);

    foreach (var name in new[]
             {
                 "journal_mode", "synchronous", "fullfsync", "checkpoint_fullfsync",
                 "auto_vacuum", "cache_size", "page_size", "wal_autocheckpoint"
             })
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        pragmas[name] = (await command.ExecuteScalarAsync(ct))?.ToString() ?? string.Empty;
    }

    return Results.Ok(new ApiInfoDto(p.DbPath, settings.ToDto(), writer.ToDto(), pragmas));
});

app.MapGet("/api/catalog", (CatalogCache catalog) => Results.Ok(catalog.Catalog));

app.MapGet("/api/stats", (SalesMetrics metrics, SaleQueue queue, DatabasePaths p)
    => Results.Ok(metrics.Snapshot(queue.Accepting, queue.Count, p)));

app.MapPost("/api/sales", async (
    SaleRequest request,
    SaleQueue queue,
    CatalogCache catalog,
    SalesMetrics metrics,
    WriterSettings writer,
    CancellationToken ct) =>
{
    if (!queue.Accepting)
        return Results.Json(new { error = "base en reset" }, statusCode: StatusCodes.Status503ServiceUnavailable);

    if (request.Quantity <= 0)
        return Results.BadRequest(new { error = "quantity debe ser > 0" });

    if (request.Sequence <= 0 || request.Sequence >= ClientSaleIds.CashierMultiplier)
        return Results.BadRequest(new { error = "sequence fuera de rango" });

    if (!catalog.HasCashier(request.CashierId))
        return Results.BadRequest(new { error = "cajero desconocido" });

    var product = catalog.Catalog.Product;
    var clientSaleId = ClientSaleIds.Encode(request.CashierId, request.Sequence);

    var completion = writer.AckMode == AckMode.Commit
        ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        : null;

    var command = new SaleCommand(
        clientSaleId,
        request.CashierId,
        product.Id,
        request.Quantity,
        product.UnitPriceCents,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        completion);

    var started = Stopwatch.GetTimestamp();
    var accepted = await queue.EnqueueAsync(command, ct);
    metrics.RecordEnqueued(Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    if (!accepted)
    {
        completion?.TrySetCanceled();
        return Results.Json(new { error = "base en reset" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (completion is null)
        return Results.Accepted($"/api/sales/{clientSaleId}", new SaleAccepted(clientSaleId, "queued"));

    try
    {
        await completion.Task.WaitAsync(ct);
        return Results.Created($"/api/sales/{clientSaleId}", new SaleAccepted(clientSaleId, "committed"));
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        // El cliente se fue, pero la venta puede commitear igualmente; el id es idempotente.
        return Results.Empty;
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/admin/reset", async (ResetService reset, CancellationToken ct)
    => Results.Ok(await reset.ResetAsync(ct)));

app.MapGet("/api/admin/verify", async (VerifyService verify, CancellationToken ct)
    => Results.Ok(await verify.VerifyAsync(ct)));

app.MapGet("/api/admin/persisted-ids", async (
    long? after,
    int? limit,
    VerifyService verify,
    CancellationToken ct) =>
{
    var take = Math.Clamp(limit ?? 20_000, 1, 50_000);
    var ids = await verify.GetPersistedIdsAsync(after ?? 0, take, ct);
    return Results.Ok(ids);
});

app.Run();
