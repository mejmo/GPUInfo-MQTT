namespace GpuInfoMqtt;

/// <summary>Keeps the last hour of power samples so the tray chart is populated as soon as it opens.</summary>
public sealed class HistoryStore
{
    private static readonly long KeepMs = (long)TimeSpan.FromHours(1).TotalMilliseconds;

    private readonly object _lock = new();
    private readonly Queue<HistoryPoint> _points = new();
    private long _last;

    public void Add(GpuReading[] gpus)
    {
        if (gpus.Length == 0) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_lock)
        {
            if (now - _last < 3000) return;
            _last = now;
            _points.Enqueue(new HistoryPoint(now, gpus.Select(g => g.Power).ToArray()));
            while (_points.Peek().T < now - KeepMs) _points.Dequeue();
        }
    }

    public HistoryPoint[] Snapshot()
    {
        lock (_lock) return [.. _points];
    }
}

/// <summary>Samples the GPUs every 5 s, independent of whether MQTT is connected.</summary>
public sealed class HistorySampler(GpuReader reader, SettingsStore store, ILogger<HistorySampler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                await reader.ReadAsync(store.Current.NvidiaSmiPath, TimeSpan.FromSeconds(4.5), stop);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogDebug("GPU sample failed: {Message}", ex.Message);
            }
        } while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false));
    }
}
