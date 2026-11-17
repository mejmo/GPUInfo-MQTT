namespace GpuInfoMqtt;

public sealed record GpuReading(
    int Index,
    string Uuid,
    string Name,
    double? Power,
    double? PowerLimit,
    double? MemoryUsed,
    double? MemoryTotal,
    double? Temperature,
    double? Fan,
    double? Utilization)
{
    public double? MemoryPercent =>
        MemoryUsed is { } u && MemoryTotal is > 0 ? Math.Round(u / MemoryTotal.Value * 100, 1) : null;
}

/// <summary>Settings as shown to the tray app (the password itself is never sent back).</summary>
public sealed record SettingsView(
    bool Enabled,
    string Host,
    int Port,
    bool UseTls,
    bool AllowUntrustedCertificates,
    string Username,
    bool HasPassword,
    string DiscoveryPrefix,
    string BaseTopic,
    string DeviceName,
    int IntervalSeconds,
    string NvidiaSmiPath);

/// <summary>Settings sent by the tray app. Password: null = keep the stored one.</summary>
public sealed record SettingsUpdate(
    bool Enabled,
    string Host,
    int Port,
    bool UseTls,
    bool AllowUntrustedCertificates,
    string Username,
    string? Password,
    bool ClearPassword,
    string DiscoveryPrefix,
    string BaseTopic,
    string DeviceName,
    int IntervalSeconds,
    string NvidiaSmiPath)
{
    public IEnumerable<string> Validate()
    {
        if (Enabled && string.IsNullOrWhiteSpace(Host)) yield return "Broker host is required.";
        if (Port is < 1 or > 65535) yield return "Port must be between 1 and 65535.";
        if (IntervalSeconds is < 2 or > 3600) yield return "Interval must be between 2 and 3600 seconds.";
        if (!IsTopicSegment(DiscoveryPrefix)) yield return "Discovery prefix must be non-empty and must not contain '+', '#' or spaces.";
        if (!IsTopicSegment(BaseTopic)) yield return "Base topic must be non-empty and must not contain '+', '#' or spaces.";
        if (string.IsNullOrWhiteSpace(DeviceName)) yield return "Device name is required.";
        if (string.IsNullOrWhiteSpace(NvidiaSmiPath)) yield return "nvidia-smi path is required.";
    }

    private static bool IsTopicSegment(string s) =>
        !string.IsNullOrWhiteSpace(s) && s.IndexOfAny(['+', '#', ' ']) < 0 && !s.StartsWith('/') && !s.EndsWith('/');
}

public sealed record StatusEvent(DateTimeOffset Time, string Level, string Text);

/// <summary>One sample: unix time in ms and power draw (W) per GPU, in GPU index order.</summary>
public sealed record HistoryPoint(long T, double?[] P);

public sealed record StatusSnapshot(
    string State,
    string? Message,
    DateTimeOffset? LastPublish,
    StatusEvent[] Events,
    GpuReading[] Gpus,
    string? GpuError,
    HistoryPoint[]? History = null,
    string? ServiceVersion = null);

public static class Commands
{
    public const string GetSettings = "getSettings";
    public const string SaveSettings = "saveSettings";
    public const string GetStatus = "getStatus";
}

public sealed record PipeRequest(string Command, SettingsUpdate? Settings = null, bool IncludeHistory = false);

public sealed record PipeResponse(
    bool Ok,
    string[]? Errors = null,
    SettingsView? Settings = null,
    StatusSnapshot? Status = null);
