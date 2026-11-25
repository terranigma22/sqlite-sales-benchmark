using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Sales.Api.Data;
using Sales.Api.Data.Entities;
using Sales.Api.Metrics;

namespace Sales.Api.Queue;

/// <summary>
/// Único consumidor del Channel. Guarda en lotes de 500; si no se llenan,
/// los vuelca al vencer FlushInterval (200 ms desde la primera venta del lote).
/// </summary>
public sealed class SalesWriterService(
    SaleQueue queue,
    IDbContextFactory<AppDbContext> dbFactory,
    SalesMetrics metrics,
    WriterMutex mutex,
    WriterGate gate,
    WriterSettings settings,
    ILogger<SalesWriterService> logger) : BackgroundService
{
    private TimeSpan FlushInterval => TimeSpan.FromMilliseconds(settings.FlushMs);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            long generation;
            List<SaleCommand> batch;

            try
            {
                await gate.WaitIfPausedAsync(stoppingToken);
                (generation, batch) = await ReadBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (batch.Count == 0)
                break;

            try
            {
                await mutex.Semaphore.WaitAsync(stoppingToken);
                try
                {
                    // Si hubo un reset mientras leíamos, este lote es de la generación anterior.
                    if (queue.Generation != generation)
                    {
                        metrics.RecordDiscarded(batch.Count);
                        foreach (var command in batch)
                        {
                            command.Completion?.TrySetException(
                                new InvalidOperationException("Lote descartado por un reset de la base."));
                        }
                        continue;
                    }

                    await PersistAsync(batch, stoppingToken);
                }
                finally
                {
                    mutex.Semaphore.Release();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                metrics.RecordFailure(batch.Count);
                foreach (var command in batch)
                    command.Completion?.TrySetException(ex);
                logger.LogError(ex, "No se pudo persistir un lote de {Count} ventas", batch.Count);
            }
        }
    }

    private async ValueTask<(long Generation, List<SaleCommand> Batch)> ReadBatchAsync(CancellationToken ct)
    {
        var batch = new List<SaleCommand>(settings.BatchSize);

        if (!await queue.Reader.WaitToReadAsync(ct))
            return (queue.Generation, batch);

        // La generación se fija cuando ya hay al menos una venta, no antes de esperar:
        // si el reset ocurre mientras la cola está vacía, este lote es de la nueva base.
        var generation = queue.Generation;

        using var flushCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        flushCts.CancelAfter(FlushInterval);

        while (batch.Count < settings.BatchSize)
        {
            if (queue.Reader.TryRead(out var sale))
            {
                batch.Add(sale);
                continue;
            }

            try
            {
                if (!await queue.Reader.WaitToReadAsync(flushCts.Token))
                    break;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                break; // venció el intervalo: se guarda el lote parcial
            }
        }

        return (generation, batch);
    }

    private async Task PersistAsync(List<SaleCommand> batch, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var persistedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.ChangeTracker.AutoDetectChangesEnabled = false;

        var rows = new Sale[batch.Count];
        for (var i = 0; i < batch.Count; i++)
        {
            var command = batch[i];
            rows[i] = new Sale
            {
                Id = command.ClientSaleId,
                CashierId = command.CashierId,
                ProductId = command.ProductId,
                Quantity = command.Quantity,
                UnitPriceCents = command.UnitPriceCents,
                TotalCents = command.UnitPriceCents * command.Quantity,
                CreatedAtUnixMs = command.CreatedAtUnixMs,
                PersistedAtUnixMs = persistedAt
            };
        }

        db.Sales.AddRange(rows);
        await db.SaveChangesAsync(ct);

        // El commit ya está en el WAL (con el synchronous configurado).
        // Solo ahora se desbloquean los requests que esperan el 201.
        foreach (var command in batch)
            command.Completion?.TrySetResult();

        metrics.RecordBatch(
            batch.Count,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            full: batch.Count == settings.BatchSize);
    }
}
