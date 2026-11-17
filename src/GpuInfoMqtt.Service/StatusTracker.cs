namespace GpuInfoMqtt;

/// <summary>In-memory runtime status reported to the tray app.</summary>
public sealed class StatusTracker
{
    private readonly object _lock = new();
    private readonly LinkedList<StatusEvent> _events = new();
    private string _state = "starting";
    private string? _message;
    private DateTimeOffset? _lastPublish;

    public void Set(string state, string? message = null)
    {
        lock (_lock) { _state = state; _message = message; }
    }

    public void Published()
    {
        lock (_lock) _lastPublish = DateTimeOffset.Now;
    }

    public void Event(string level, string text)
    {
        lock (_lock)
        {
            // Don't flood the list with the same repeating error.
            if (_events.First?.Value is { } last && last.Text == text && last.Level == level)
                _events.RemoveFirst();
            _events.AddFirst(new StatusEvent(DateTimeOffset.Now, level, text));
            while (_events.Count > 30) _events.RemoveLast();
        }
    }

    public StatusSnapshot Snapshot(GpuReading[] gpus, string? gpuError)
    {
        lock (_lock) return new StatusSnapshot(_state, _message, _lastPublish, [.. _events], gpus, gpuError);
    }
}
