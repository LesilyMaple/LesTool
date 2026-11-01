using System.Threading.Channels;
using Serilog;

namespace LesTool.Platform;

public enum QueryState { Ready, TimedOut, Busy, Cancelled, Failed }
public sealed record QueryResult<T>(QueryState State, T? Value = default, Exception? Error = null);

/// <summary>One MTA worker. Timing out stops waiting, never creates a replacement for a hung COM call.</summary>
public sealed class BoundedQueryExecutor : IDisposable
{
    private sealed class Work(Action run, Func<bool> expired, Action cancel)
    {
        internal Action Run { get; } = run;
        internal Func<bool> Expired { get; } = expired;
        internal Action Cancel { get; } = cancel;
    }
    private readonly object _gate = new();
    private readonly Channel<Work> _queue;
    private Work? _active;
    private bool _blocked, _disposed;
    public BoundedQueryExecutor(int capacity = 8)
    {
        _queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(capacity) { SingleReader = false, FullMode = BoundedChannelFullMode.Wait });
        var worker = new Thread(Loop) { IsBackground = true, Name = "LesTool.UIA" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }
    public async Task<QueryResult<T>> RunAsync<T>(Func<T> query, TimeSpan budget, CancellationToken token = default)
    {
        if (token.IsCancellationRequested) return new(QueryState.Cancelled);
        var completion = new TaskCompletionSource<QueryResult<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new Work(() =>
        {
            try { completion.TrySetResult(new(QueryState.Ready, query())); }
            catch (Exception e) { completion.TrySetResult(new(QueryState.Failed, Error: e)); }
        }, () => completion.Task.IsCompleted, () => completion.TrySetResult(new(QueryState.Cancelled)));
        lock (_gate)
        {
            if (_disposed || _blocked || !_queue.Writer.TryWrite(work)) return new(QueryState.Busy);
        }
        try { return await completion.Task.WaitAsync(budget, token).ConfigureAwait(false); }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            var result = new QueryResult<T>(e is TimeoutException ? QueryState.TimedOut : QueryState.Cancelled);
            lock (_gate)
            {
                completion.TrySetResult(result);
                if (ReferenceEquals(_active, work)) _blocked = true;
            }
            return result;
        }
    }
    private void Loop()
    {
        try
        {
            while (_queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                while (_queue.Reader.TryRead(out var work))
                {
                    lock (_gate)
                    {
                        if (_disposed) { work.Cancel(); continue; }
                        if (work.Expired()) continue;
                        _active = work;
                    }
                    try { work.Run(); }
                    finally { lock (_gate) { _active = null; _blocked = false; } }
                }
        }
        catch (Exception e) { Log.Error(e, "UIA 工作线程退出"); }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true; _queue.Writer.TryComplete();
            _active?.Cancel();
            while (_queue.Reader.TryRead(out var queued)) queued.Cancel();
        }
    }
}

public static class UiaQueries
{
    private static readonly Lazy<BoundedQueryExecutor> Shared = new(() => new());
    public static BoundedQueryExecutor Executor => Shared.Value;
    public static void Stop() { if (Shared.IsValueCreated) Shared.Value.Dispose(); }
}
