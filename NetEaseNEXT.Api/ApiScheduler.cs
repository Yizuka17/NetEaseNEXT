namespace NetEaseNEXT.Api;

// FIFO write barriers with up to four independent reads. Cancellation affects
// one request only; there is no worker process or shared pipe to terminate.
public sealed class ApiScheduler
{
    private sealed record Work(bool Write, Func<Task> Run);
    private readonly Queue<Work> _queue = new();
    private readonly object _gate = new();
    private int _active;
    private bool _writing;
    public Task<T> Schedule<T>(bool write, Func<Task<T>> operation, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Run()
        {
            try { ct.ThrowIfCancellationRequested(); completion.TrySetResult(await operation().ConfigureAwait(false)); }
            catch (OperationCanceledException) { completion.TrySetCanceled(ct); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }
        lock (_gate) { _queue.Enqueue(new(write, Run)); Pump(); }
        return completion.Task.WaitAsync(ct);
    }
    private void Pump()
    {
        while (!_writing && _active < 4 && _queue.TryPeek(out var next))
        {
            if (next.Write && _active > 0) break;
            _queue.Dequeue(); _active++; _writing = next.Write;
            _ = Task.Run(async () =>
            {
                try { await next.Run().ConfigureAwait(false); }
                finally { lock (_gate) { _active--; if (next.Write) _writing = false; Pump(); } }
            });
            if (next.Write) break;
        }
    }
}
