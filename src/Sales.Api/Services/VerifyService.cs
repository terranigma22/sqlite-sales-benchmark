using System.Data;
using Microsoft.EntityFrameworkCore;
using Sales.Api.Data;
using Sales.Contracts;

namespace Sales.Api.Services;

/// <summary>
/// Comprueba lo que quedó en la base: conteo, suma de importes, duplicados,
/// integridad y reparto por cajero.
/// </summary>
public sealed class VerifyService(
    IDbContextFactory<AppDbContext> dbFactory,
    DatabasePaths paths,
    CatalogCache catalog)
{
    public async Task<VerifyResponse> VerifyAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var count = await db.Sales.LongCountAsync(ct);
        var distinctIds = await db.Sales.Select(x => x.Id).Distinct().LongCountAsync(ct);
        var sumQuantity = await db.Sales.SumAsync(x => (long)x.Quantity, ct);
        var sumTotalCents = await db.Sales.SumAsync(x => x.TotalCents, ct);

        // Proyección a tipo anónimo: EF Core SQLite no traduce el constructor del record
        // dentro de un GroupBy. Se materializa y se mapea a CashierCount en memoria.
        var grouped = await db.Sales
            .GroupBy(x => x.CashierId)
            .Select(g => new
            {
                CashierId = g.Key,
                Count = g.Count(),
                SumCents = g.Sum(x => x.TotalCents)
            })
            .OrderBy(x => x.CashierId)
            .ToListAsync(ct);

        var perCashier = grouped
            .Select(x => new CashierCount(x.CashierId, x.Count, x.SumCents))
            .ToList();

        var integrity = await IntegrityCheckAsync(db, ct);

        return new VerifyResponse(
            Count: count,
            DistinctIds: distinctIds,
            SumQuantity: sumQuantity,
            SumTotalCents: sumTotalCents,
            Duplicates: count - distinctIds,
            IntegrityCheck: integrity,
            DbBytes: DatabasePaths.FileSize(paths.DbPath),
            WalBytes: DatabasePaths.FileSize(paths.WalPath),
            PerCashier: perCashier,
            Product: catalog.Catalog.Product);
    }

    public async Task<IReadOnlyList<long>> GetPersistedIdsAsync(long after, int limit, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var start = after;

        return await db.Sales
            .Where(x => x.Id > start)
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    private static async Task<string> IntegrityCheckAsync(AppDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        return (string?)await command.ExecuteScalarAsync(ct) ?? "sin respuesta";
    }
}
