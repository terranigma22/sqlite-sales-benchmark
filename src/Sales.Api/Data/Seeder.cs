using Microsoft.EntityFrameworkCore;
using Sales.Api.Data.Entities;

namespace Sales.Api.Data;

public static class Seeder
{
    public const long ProductId = 1;
    public const long DefaultUnitPriceCents = 1999;
    public const int DefaultCashierCount = 200;

    public static async Task SeedAsync(
        AppDbContext db,
        int cashierCount,
        long unitPriceCents,
        CancellationToken ct = default)
    {
        if (await db.Products.AnyAsync(ct))
            return;

        db.Products.Add(new Product
        {
            Id = ProductId,
            Name = "Test Product",
            UnitPriceCents = unitPriceCents
        });

        for (var i = 1; i <= cashierCount; i++)
        {
            db.Cashiers.Add(new Cashier
            {
                Id = i,
                Name = $"Cashier {i:000}"
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
