namespace Sales.Api.Data.Entities;

public sealed class Product
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public long UnitPriceCents { get; set; }
}

public sealed class Cashier
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class Sale
{
    // El id lo aporta el cliente (idempotencia) y es la PK: INTEGER PRIMARY KEY, sin AUTOINCREMENT.
    public long Id { get; set; }
    public long CashierId { get; set; }
    public long ProductId { get; set; }
    public int Quantity { get; set; }
    public long UnitPriceCents { get; set; }
    public long TotalCents { get; set; }
    public long CreatedAtUnixMs { get; set; }
    public long PersistedAtUnixMs { get; set; }
}
