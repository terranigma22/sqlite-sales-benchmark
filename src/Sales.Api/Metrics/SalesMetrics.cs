using System.Collections.Concurrent;
using Sales.Api.Data;
using Sales.Contracts;

namespace Sales.Api.Metrics;

/// <summary>
/// Contadores e histogramas en memoria. El snapshot se sirve por /api/stats
/// y el cliente lo muestrea cada 250 ms.
/// </summary>
public sealed class SalesMetrics
{
    private readonly ConcurrentQueue<double> _batchSaveMs = new();
    private readonly object _snapshotSync = new();

    private long _enqueued;
    private long _persisted;
    private long _failed;
    private long _discarded;
    private long _batches;
    private long _fullBatches;
    private long _partialBatches;
    private long _batchRows;
    private long _batchSaveMicros;
    private long _enqueueWaitMicros;
    private long _maxEnqueueWaitMicros;
    private long _maxBatchSaveMicros;

    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _lastPersistedAt;

    public void RecordEnqueued(double waitMs)
    {
        Interlocked.Increment(ref _enqueued);
        var micros = (long)(waitMs * 1000);
        Interlocked.Add(ref _enqueueWaitMicros, micros);
        UpdateMax(ref _maxEnqueueWaitMicros, micros);
        _startedAt ??= DateTimeOffset.UtcNow;
    }

    public void RecordBatch(int size, double saveMs, bool full)
    {
        Interlocked.Increment(ref _batches);
        Interlocked.Add(ref _persisted, size);
        Interlocked.Add(ref _batchRows, size);

        var micros = (long)(saveMs * 1000);
        Interlocked.Add(ref _batchSaveMicros, micros);
        UpdateMax(ref _maxBatchSaveMicros, micros);

        if (full)
            Interlocked.Increment(ref _fullBatches);
        else
            Interlocked.Increment(ref _partialBatches);

        _batchSaveMs.Enqueue(saveMs);
        _lastPersistedAt = DateTimeOffset.UtcNow;
    }

    public void RecordFailure(int sales)
    {
        Interlocked.Add(ref _failed, sales);
        _lastPersistedAt = DateTimeOffset.UtcNow;
    }

    public void RecordDiscarded(int sales)
        => Interlocked.Add(ref _discarded, sales);

    public StatsSnapshot Snapshot(bool accepting, int queueLength, DatabasePaths paths)
    {
        var batches = Interlocked.Read(ref _batches);
        var batchRows = Interlocked.Read(ref _batchRows);
        var batchSaveMicros = Interlocked.Read(ref _batchSaveMicros);
        var enqueued = Interlocked.Read(ref _enqueued);
        var enqueueWaitMicros = Interlocked.Read(ref _enqueueWaitMicros);

        var batchSaves = _batchSaveMs.ToArray();
        var p95BatchSave = Percentile(batchSaves, 0.95);

        return new StatsSnapshot(
            Accepting: accepting,
            Enqueued: enqueued,
            Persisted: Interlocked.Read(ref _persisted),
            Failed: Interlocked.Read(ref _failed),
            Discarded: Interlocked.Read(ref _discarded),
            QueueLength: queueLength,
            Batches: batches,
            FullBatches: Interlocked.Read(ref _fullBatches),
            PartialBatches: Interlocked.Read(ref _partialBatches),
            AvgBatchSize: batches == 0 ? 0 : (double)batchRows / batches,
            AvgBatchSaveMs: batches == 0 ? 0 : batchSaveMicros / 1000.0 / batches,
            P95BatchSaveMs: p95BatchSave,
            AvgEnqueueWaitMs: enqueued == 0 ? 0 : enqueueWaitMicros / 1000.0 / enqueued,
            MaxEnqueueWaitMs: Interlocked.Read(ref _maxEnqueueWaitMicros) / 1000.0,
            DbBytes: DatabasePaths.FileSize(paths.DbPath),
            WalBytes: DatabasePaths.FileSize(paths.WalPath),
            StartedAt: _startedAt?.ToString("O"),
            LastPersistedAt: _lastPersistedAt?.ToString("O"));
    }

    public void Reset()
    {
        lock (_snapshotSync)
        {
            Interlocked.Exchange(ref _enqueued, 0);
            Interlocked.Exchange(ref _persisted, 0);
            Interlocked.Exchange(ref _failed, 0);
            Interlocked.Exchange(ref _discarded, 0);
            Interlocked.Exchange(ref _batches, 0);
            Interlocked.Exchange(ref _fullBatches, 0);
            Interlocked.Exchange(ref _partialBatches, 0);
            Interlocked.Exchange(ref _batchRows, 0);
            Interlocked.Exchange(ref _batchSaveMicros, 0);
            Interlocked.Exchange(ref _enqueueWaitMicros, 0);
            Interlocked.Exchange(ref _maxEnqueueWaitMicros, 0);
            Interlocked.Exchange(ref _maxBatchSaveMicros, 0);

            while (_batchSaveMs.TryDequeue(out _))
            {
            }

            _startedAt = null;
            _lastPersistedAt = null;
        }
    }

    private static void UpdateMax(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    private static double Percentile(double[] values, double percentile)
    {
        if (values.Length == 0)
            return 0;

        Array.Sort(values);
        var index = (int)Math.Ceiling(percentile * values.Length) - 1;
        return values[Math.Clamp(index, 0, values.Length - 1)];
    }
}
