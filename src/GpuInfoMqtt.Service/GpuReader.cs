using System.Diagnostics;
using System.Globalization;

namespace GpuInfoMqtt;

/// <summary>Runs nvidia-smi; caches the last result so the worker and the tray app don't both spawn it.</summary>
public sealed class GpuReader(HistoryStore history)
{
    private const string Fields =
        "index,uuid,name,power.draw,power.limit,memory.used,memory.total,temperature.gpu,fan.speed,utilization.gpu";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTime At, string Path, GpuReading[] Gpus)? _cache;

    public string? LastError { get; private set; }

    public async Task<GpuReading[]> ReadAsync(string nvidiaSmi, TimeSpan maxAge, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache is { } c && c.Path == nvidiaSmi && DateTime.UtcNow - c.At < maxAge) return c.Gpus;
            try
            {
                var gpus = await RunAsync(nvidiaSmi, ct);
                _cache = (DateTime.UtcNow, nvidiaSmi, gpus);
                LastError = null;
                history.Add(gpus);
                return gpus;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _cache = null;
                LastError = ex.Message;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<GpuReading[]> RunAsync(string nvidiaSmi, CancellationToken ct)
    {
        var exe = NvidiaSmiLocator.Resolve(nvidiaSmi);
        var psi = new ProcessStartInfo(exe, $"--query-gpu={Fields} --format=csv,noheader,nounits")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start '{nvidiaSmi}'.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await p.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (!ct.IsCancellationRequested) throw new TimeoutException("nvidia-smi did not respond within 15 seconds.");
            throw;
        }

        var output = await stdout;
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"nvidia-smi exited with {p.ExitCode}: {(await stderr + output).Trim()}");

        return Parse(output);
    }

    public static GpuReading[] Parse(string csv)
    {
        var list = new List<GpuReading>();
        foreach (var raw in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split(',').Select(x => x.Trim()).ToArray();
            if (parts.Length < 10) continue;
            // Name is the only field that could contain a comma; the 7 numeric fields follow it.
            var tail = parts[^7..];
            list.Add(new GpuReading(
                int.TryParse(parts[0], out var idx) ? idx : list.Count,
                parts[1],
                string.Join(",", parts[2..^7]),
                Num(tail[0]), Num(tail[1]), Num(tail[2]), Num(tail[3]), Num(tail[4]), Num(tail[5]), Num(tail[6])));
        }
        return [.. list];
    }

    // nvidia-smi prints "[N/A]" / "[Not Supported]" for unavailable values.
    private static double? Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}
