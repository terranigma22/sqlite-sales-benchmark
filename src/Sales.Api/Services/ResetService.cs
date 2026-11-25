using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sales.Api.Data;
using Sales.Api.Metrics;
using Sales.Api.Queue;
using Sales.Contracts;

namespace Sales.Api.Services;

/// <summary>
/// Deja la base limpia antes de cada corrida: cierra conexiones y pool, borra
/// sales.db / -wal / -shm, recrea el esquema, siembra 200 cajeros + 1 producto
/// y reinicia los contadores.
/// </summary>
public sealed class ResetService(
    IDbContextFactory<AppDbContext> dbFactory,
    SaleQueue queue,
    SalesMetrics metrics,
    WriterMutex mutex,
    WriterGate gate,
    CatalogCache catalog,
    DatabasePaths paths,
    SqliteSettings sqliteSettings,
    IConfiguration configuration,
    ILogger<ResetService> logger)
{
    private readonly SemaphoreSlim _resetGate = new(1, 1);

    public async Task<ResetResponse> ResetAsync(CancellationToken ct)
    {
        await _resetGate.WaitAsync(ct);
        try
        {
            gate.Pause();
            queue.SetAccepting(false);

            // Espera a que termine el lote en vuelo y bloquea al escritor durante el borrado.
            await mutex.Semaphore.WaitAsync(ct);
            try
            {
                queue.BumpGeneration();
                queue.Drain();

                SqliteConnection.ClearAllPools();
                DeleteIfExists(paths.DbPath);
                DeleteIfExists(paths.WalPath);
                DeleteIfExists(paths.ShmPath);

                await using var db = await dbFactory.CreateDbContextAsync(ct);
                await db.Database.EnsureCreatedAsync(ct);
                await db.Database.ExecuteSqlRawAsync(sqliteSettings.JournalPragma, ct);

                var cashierCount = configuration.GetValue("Benchmark:CashierCount", Seeder.DefaultCashierCount);
                var unitPriceCents = configuration.GetValue("Benchmark:UnitPriceCents", Seeder.DefaultUnitPriceCents);
                await Seeder.SeedAsync(db, cashierCount, unitPriceCents, ct);
                await catalog.ReloadAsync(db, ct);

                metrics.Reset();
                gate.Resume();
                queue.SetAccepting(true);

                logger.LogInformation(
                    "Base reiniciada en {DbPath}: {Cashiers} cajeros, producto a {Price} céntimos",
                    paths.DbPath,
                    cashierCount,
                    unitPriceCents);

                return new ResetResponse(paths.DbPath, catalog.Catalog.Product, catalog.Catalog.CashierIds.Count);
            }
            finally
            {
                mutex.Semaphore.Release();
            }
        }
        finally
        {
            gate.Resume();
            _resetGate.Release();
        }
    }

    /// <summary>Arranque normal: crea si falta, activa WAL y siembra si está vacía. No borra.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.DbPath)!);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Database.EnsureCreatedAsync(ct);
        await db.Database.ExecuteSqlRawAsync(sqliteSettings.JournalPragma, ct);

        var cashierCount = configuration.GetValue("Benchmark:CashierCount", Seeder.DefaultCashierCount);
        var unitPriceCents = configuration.GetValue("Benchmark:UnitPriceCents", Seeder.DefaultUnitPriceCents);
        await Seeder.SeedAsync(db, cashierCount, unitPriceCents, ct);
        await catalog.ReloadAsync(db, ct);

        logger.LogInformation(
            "API lista. Base: {DbPath}. SQLite: journal={Journal}, synchronous={Sync}, fullfsync={Full}, checkpoint_fullfsync={CheckpointFull}",
            paths.DbPath,
            sqliteSettings.JournalMode,
            sqliteSettings.Synchronous,
            sqliteSettings.FullFsync,
            sqliteSettings.CheckpointFullFsync);
    }

    private static void DeleteIfExists(string path)
    {
        for (var attempt = 0; attempt < 10 && File.Exists(path); attempt++)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(50);
            }
        }

        if (File.Exists(path))
            throw new IOException($"No se pudo borrar {path}; ¿hay otra instancia usando la base?");
    }
}
