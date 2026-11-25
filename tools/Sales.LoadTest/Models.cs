using Sales.Contracts;

namespace Sales.LoadTest;

public readonly record struct SaleResult(
    long CashierId,
    int Sequence,
    int HttpStatus,
    double LatencyMs,
    string? Error);

public sealed record LatencyStats(
    double AvgMs,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs);

public sealed record HistogramBin(double StartMs, double EndMs, int Count);

public sealed record StatusCount(int Status, int Count);

public sealed record TimelineSample(
    double ElapsedMs,
    int QueueLength,
    long Persisted,
    long Enqueued,
    long Batches);

public sealed record ExpectedTotals(long Count, long Quantity, long TotalCents);

public sealed record EnvironmentInfo(
    string MachineName,
    string Os,
    string Architecture,
    int ProcessorCount,
    string Runtime,
    string Framework,
    string WorkingDirectory);

public sealed record RunReport
{
    public required string RunId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset FinishedAt { get; init; }
    public required double DurationSeconds { get; init; }
    public required double DrainSeconds { get; init; }
    public required double TotalSeconds { get; init; }
    public required double VerifySeconds { get; init; }
    public required double ReportSeconds { get; init; }
    public required bool Pass { get; init; }
    public required Options Options { get; init; }
    public required EnvironmentInfo Environment { get; init; }
    public required CatalogResponse Catalog { get; init; }
    public required ApiInfoDto ApiInfo { get; init; }
    public required StatsSnapshot Baseline { get; init; }
    public required StatsSnapshot Final { get; init; }
    public required VerifyResponse Verify { get; init; }
    public required int Attempted { get; init; }
    public required int Accepted { get; init; }
    public required int Persisted { get; init; }
    public required int Lost { get; init; }
    public required int Rejected { get; init; }
    public required int Recovered { get; init; }
    public required ExpectedTotals Expected { get; init; }
    public required double Throughput { get; init; }
    public required LatencyStats Latency { get; init; }
    public required IReadOnlyList<StatusCount> Statuses { get; init; }
    public required IReadOnlyList<TimelineSample> Timeline { get; init; }
    public required IReadOnlyList<HistogramBin> Histogram { get; init; }
    public required IReadOnlyList<SaleResult> Missing { get; init; }
    public required IReadOnlyList<SaleResult> Slowest { get; init; }
    public required bool PerCashierOk { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
    public required string CsvFile { get; init; }
    public required string JsonFile { get; init; }
}
