namespace Sales.Contracts;

/// <summary>
/// El id de venta lo genera el cliente: cashierId * 1_000_000 + secuencia.
/// Es la PK de la tabla y, a la vez, la clave de idempotencia de la prueba.
/// </summary>
public static class ClientSaleIds
{
    public const long CashierMultiplier = 1_000_000;

    public static long Encode(long cashierId, int sequence)
        => cashierId * CashierMultiplier + sequence;

    public static long CashierOf(long clientSaleId)
        => clientSaleId / CashierMultiplier;

    public static int SequenceOf(long clientSaleId)
        => (int)(clientSaleId % CashierMultiplier);
}

/// <summary>Lo que manda el cajero. Quantity fijo a 1 en la prueba base.</summary>
public sealed record SaleRequest(long CashierId, int Sequence, int Quantity = 1);

public sealed record SaleAccepted(long ClientSaleId, string Status);

public sealed record ProductDto(long Id, string Name, long UnitPriceCents);

public sealed record CatalogResponse(ProductDto Product, IReadOnlyList<long> CashierIds);

public sealed record StatsSnapshot(
    bool Accepting,
    long Enqueued,
    long Persisted,
    long Failed,
    long Discarded,
    int QueueLength,
    long Batches,
    long FullBatches,
    long PartialBatches,
    double AvgBatchSize,
    double AvgBatchSaveMs,
    double P95BatchSaveMs,
    double AvgEnqueueWaitMs,
    double MaxEnqueueWaitMs,
    long DbBytes,
    long WalBytes,
    string? StartedAt,
    string? LastPersistedAt);

public sealed record CashierCount(long CashierId, long Count, long SumCents);

public sealed record VerifyResponse(
    long Count,
    long DistinctIds,
    long SumQuantity,
    long SumTotalCents,
    long Duplicates,
    string IntegrityCheck,
    long DbBytes,
    long WalBytes,
    IReadOnlyList<CashierCount> PerCashier,
    ProductDto Product);

public sealed record ResetResponse(string DbPath, ProductDto Product, int CashierCount);

public sealed record SqliteConfigDto(
    string JournalMode,
    string Synchronous,
    bool FullFsync,
    bool CheckpointFullFsync,
    int CacheSizeKb,
    int BusyTimeoutMs);

public sealed record WriterConfigDto(
    int BatchSize,
    int FlushMs,
    string AckMode);

public sealed record ApiInfoDto(
    string DbPath,
    SqliteConfigDto Config,
    WriterConfigDto Writer,
    IReadOnlyDictionary<string, string> Pragmas);
