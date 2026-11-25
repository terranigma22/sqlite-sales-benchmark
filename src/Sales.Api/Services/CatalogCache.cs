using Microsoft.EntityFrameworkCore;
using Sales.Api.Data;
using Sales.Contracts;

namespace Sales.Api.Services;

/// <summary>
/// Catálogo en memoria (1 producto + N cajeros) para que el endpoint no consulte la BD
/// en cada request.
/// </summary>
public sealed class CatalogCache
{
    private sealed record State(CatalogResponse Catalog, HashSet<long> CashierIds);

    private volatile State? _state;

    public CatalogResponse Catalog
        => _state?.Catalog ?? throw new InvalidOperationException("El catálogo aún no se ha cargado.");

    public bool HasCashier(long cashierId)
        => _state?.CashierIds.Contains(cashierId) == true;

    public async Task ReloadAsync(AppDbContext db, CancellationToken ct = default)
    {
        var product = await db.Products.AsNoTracking().OrderBy(x => x.Id).FirstAsync(ct);
        var cashierIds = await db.Cashiers
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync(ct);

        var productDto = new ProductDto(product.Id, product.Name, product.UnitPriceCents);
        _state = new State(
            new CatalogResponse(productDto, cashierIds),
            cashierIds.ToHashSet());
    }
}
