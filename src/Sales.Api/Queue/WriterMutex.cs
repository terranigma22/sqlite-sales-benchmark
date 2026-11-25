namespace Sales.Api.Queue;

/// <summary>
/// Serializa el guardado de lotes con el reset de la base. Un solo escritor SQLite.
/// </summary>
public sealed class WriterMutex
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
}
