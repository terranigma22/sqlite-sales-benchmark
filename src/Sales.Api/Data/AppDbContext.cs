using Microsoft.EntityFrameworkCore;
using Sales.Api.Data.Entities;

namespace Sales.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<Cashier> Cashiers => Set<Cashier>();
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(entity =>
        {
            entity.ToTable("Products");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Name).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<Cashier>(entity =>
        {
            entity.ToTable("Cashiers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Name).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<Sale>(entity =>
        {
            entity.ToTable("Sales");
            entity.HasKey(x => x.Id);
            // Sin AUTOINCREMENT: la PK es el id del cliente y se inserta explícita.
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.CashierId).IsRequired();
            entity.Property(x => x.ProductId).IsRequired();
            entity.Property(x => x.Quantity).IsRequired();
            entity.Property(x => x.UnitPriceCents).IsRequired();
            entity.Property(x => x.TotalCents).IsRequired();
            entity.Property(x => x.CreatedAtUnixMs).IsRequired();
            entity.Property(x => x.PersistedAtUnixMs).IsRequired();
        });
    }
}
