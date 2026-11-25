using System.Threading.Channels;

namespace Sales.Api.Queue;

/// <summary>
/// Venta ya validada, camino del Channel al escritor de SQLite.
/// </summary>
public readonly record struct SaleCommand(
    long ClientSaleId,
    long CashierId,
    long ProductId,
    int Quantity,
    long UnitPriceCents,
    long CreatedAtUnixMs,
    TaskCompletionSource? Completion = null);

/// <summary>
/// Channel acotado a 10.000. Con FullMode.Wait, el endpoint que llama a WriteAsync
/// se queda esperando cuando la cola está llena: es el backpressure que frena al cajero.
/// </summary>
public sealed class SaleQueue
{
    public const int Capacity = 10_000;

    private readonly object _sync = new();
    private readonly Channel<SaleCommand> _channel = Channel.CreateBounded<SaleCommand>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private volatile bool _accepting = true;
    private long _generation;

    public bool Accepting => _accepting;
    public long Generation => Interlocked.Read(ref _generation);
    public int Count => _channel.Reader.Count;

    public ChannelReader<SaleCommand> Reader => _channel.Reader;

    public ValueTask<bool> EnqueueAsync(SaleCommand command, CancellationToken ct)
    {
        if (!_accepting)
            return ValueTask.FromResult(false);

        return WriteAsync(command, ct);
    }

    private async ValueTask<bool> WriteAsync(SaleCommand command, CancellationToken ct)
    {
        try
        {
            await _channel.Writer.WriteAsync(command, ct);
            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    public void SetAccepting(bool value) => _accepting = value;

    public long BumpGeneration() => Interlocked.Increment(ref _generation);

    /// <summary>Solo se usa durante un reset, cuando no hay carga en curso.</summary>
    public int Drain()
    {
        var drained = 0;
        while (_channel.Reader.TryRead(out var command))
        {
            command.Completion?.TrySetException(
                new InvalidOperationException("Venta descartada por un reset de la base."));
            drained++;
        }

        return drained;
    }
}
