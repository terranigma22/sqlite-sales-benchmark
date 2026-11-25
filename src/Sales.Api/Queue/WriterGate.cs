namespace Sales.Api.Queue;

/// <summary>
/// Pausa al escritor mientras el reset borra y recrea la base. Evita que un lote
/// leído antes del reset se guarde después en la base nueva.
/// </summary>
public sealed class WriterGate
{
    private volatile bool _paused;
    private TaskCompletionSource _resume = CreateCompleted();

    public async Task WaitIfPausedAsync(CancellationToken ct)
    {
        if (!_paused)
            return;

        await _resume.Task.WaitAsync(ct);
    }

    public void Pause()
    {
        // El TCS nuevo se publica antes de marcar _paused para que nadie
        // se quede esperando en el TCS ya completado.
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _resume = resume;
        _paused = true;
    }

    public void Resume()
    {
        _paused = false;
        _resume.TrySetResult();
    }

    private static TaskCompletionSource CreateCompleted()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
