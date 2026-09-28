namespace FantasyMontior.Core;

public sealed class MonitoringService : IAsyncDisposable
{
    private readonly Func<IHardwareBackend> _createBackend;
    private long _intervalTicks;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _lifecycle = new();
    private Task? _worker;
    private bool _stopping;
    private int _retry;
    private MonitoringSnapshot _latest = MonitoringSnapshot.Empty;

    public MonitoringService(Func<IHardwareBackend>? createBackend = null, TimeSpan? interval = null)
    {
        _createBackend = createBackend ?? (() => new LibreHardwareBackend());
        var initial = interval ?? TimeSpan.FromSeconds(1);
        if (initial <= TimeSpan.Zero || initial.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(interval));
        _intervalTicks = initial.Ticks;
    }

    public MonitoringSnapshot Latest => Volatile.Read(ref _latest);
    // Subscribers run on the collector worker and must only do bounded in-memory work.
    public event Action<MonitoringSnapshot, TimeSpan>? SnapshotPublished;
    public TimeSpan Interval => TimeSpan.FromTicks(Interlocked.Read(ref _intervalTicks));

    public void SetInterval(TimeSpan interval)
    {
        if (interval < TimeSpan.FromSeconds(1) || interval > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(interval));
        lock (_lifecycle)
        {
            if (_stopping) return;
            if (Interval == interval) return;
            Interlocked.Exchange(ref _intervalTicks, interval.Ticks);
            if (_worker is not null && _wake.CurrentCount == 0) _wake.Release();
        }
    }

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            _worker ??= Task.Run(RunAsync);
        }
    }

    public void RetryDiscovery()
    {
        lock (_lifecycle)
        {
            if (_stopping) return;
            Interlocked.Exchange(ref _retry, 1);
            if (_wake.CurrentCount == 0) _wake.Release();
        }
    }

    private void Publish(MonitoringSnapshot snapshot)
    {
        SnapshotPublished?.Invoke(snapshot, Interval);
        Volatile.Write(ref _latest, snapshot);
    }

    private void PublishFailure(string message) => Publish(Latest with
    {
        CapturedAt = DateTimeOffset.UtcNow, Status = "Collection failed",
        Errors = [message], Sensors = [.. Latest.Sensors.Select(s => s with { Error = message })]
    });

    private async Task RunAsync()
    {
        IHardwareBackend? backend = null;
        var collector = new SnapshotCollector();
        var attemptedOpen = false;
        var cleanupPending = false;

        bool CloseBackend()
        {
            try
            {
                backend?.Dispose();
                backend = null;
                cleanupPending = false;
                return true;
            }
            catch (Exception ex)
            {
                cleanupPending = true;
                PublishFailure($"Hardware cleanup failed: {ex.Message}. Retry cleanup before reopening hardware.");
                return false;
            }
        }

        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (Interlocked.Exchange(ref _retry, 0) != 0)
                {
                    if (CloseBackend())
                    {
                        collector = new SnapshotCollector();
                        attemptedOpen = false;
                        Publish(Latest with { Status = "Initializing", Sensors = [.. Latest.Sensors.Select(s => s with { Error = "Rediscovering hardware" })] });
                    }
                }

                if (!attemptedOpen)
                {
                    attemptedOpen = true;
                    try
                    {
                        backend = _createBackend();
                        backend.Open();
                    }
                    catch (Exception ex)
                    {
                        PublishFailure($"Hardware discovery failed: {ex.GetType().Name}: {ex.Message}");
                        CloseBackend();
                    }
                }

                if (backend is not null && !cleanupPending && !_stop.IsCancellationRequested)
                {
                    try { Publish(collector.Collect(backend.Hardware, DateTimeOffset.UtcNow)); }
                    catch (Exception ex) { PublishFailure($"Collection failed: {ex.GetType().Name}: {ex.Message}"); }
                }
                await _wake.WaitAsync(Interval, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            CloseBackend();
            Publish(Latest with { Status = "Stopped", Sensors = [.. Latest.Sensors.Select(s => s with { Error = s.Error ?? "Monitoring stopped" })] });
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? worker;
        lock (_lifecycle)
        {
            _stopping = true;
            _stop.Cancel();
            worker = _worker;
        }
        if (worker is not null) await worker.ConfigureAwait(false);
        // Primitives remain managed-only so concurrent/idempotent shutdown calls are safe.
    }
}
