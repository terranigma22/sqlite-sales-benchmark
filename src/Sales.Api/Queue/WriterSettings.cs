using Sales.Contracts;

namespace Sales.Api.Queue;

public enum AckMode
{
    Enqueue,
    Commit
}

/// <summary>
/// Configuración del consumidor del Channel:
///   WRITER_BATCH_SIZE=500
///   WRITER_FLUSH_MS=200
///   ACK_MODE=commit|enqueue
///
/// commit = el endpoint responde 201 solo después del SaveChanges durable.
/// enqueue = responde 202 al encolar (comportamiento antiguo; puede perder ventas
///           encoladas si el proceso muere).
/// </summary>
public sealed class WriterSettings
{
    public int BatchSize { get; init; } = 500;
    public int FlushMs { get; init; } = 200;
    public AckMode AckMode { get; init; } = AckMode.Commit;

    public WriterConfigDto ToDto()
        => new(BatchSize, FlushMs, AckMode.ToString().ToLowerInvariant());

    public static WriterSettings FromConfiguration(IConfiguration configuration)
    {
        var batchSize = configuration.GetValue("WRITER_BATCH_SIZE", 500);
        var flushMs = configuration.GetValue("WRITER_FLUSH_MS", 200);
        var ackText = (configuration["ACK_MODE"]
                       ?? configuration["Writer:AckMode"]
                       ?? "commit").Trim();

        if (batchSize is < 1 or > 50_000)
            throw new InvalidOperationException("WRITER_BATCH_SIZE debe estar entre 1 y 50.000.");

        if (flushMs is < 1 or > 5_000)
            throw new InvalidOperationException("WRITER_FLUSH_MS debe estar entre 1 y 5.000.");

        if (!Enum.TryParse<AckMode>(ackText, ignoreCase: true, out var ackMode))
            throw new InvalidOperationException($"ACK_MODE inválido: '{ackText}'. Usa commit o enqueue.");

        return new WriterSettings
        {
            BatchSize = batchSize,
            FlushMs = flushMs,
            AckMode = ackMode
        };
    }
}
