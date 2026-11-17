using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace GpuInfoMqtt;

/// <summary>
/// Local named pipe the tray app uses to read status and change settings.
/// Only interactively logged-on local users can connect (network logons are denied).
/// The password is write-only over this channel: it is never sent back to the client.
/// </summary>
public sealed class PipeServer(
    SettingsStore store, StatusTracker status, GpuReader gpus, HistoryStore history, ILogger<PipeServer> log)
    : BackgroundService
{
    private const int MaxInstances = 4;
    private static readonly string ServiceVersion = typeof(PipeServer).Assembly.GetName().Version?.ToString(3) ?? "?";

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var security = BuildSecurity();
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    PipeProtocol.PipeName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 0, 0, security);
                await pipe.WaitForConnectionAsync(stop);
                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => HandleAsync(connected, stop), stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Pipe server error");
                await Task.Delay(1000, stop).ContinueWith(_ => { });
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        await using var _ = pipe;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            var request = await PipeProtocol.ReadAsync<PipeRequest>(pipe, timeout.Token, maxBytes: 64 * 1024);
            string client;
            try { client = pipe.GetImpersonationUserName(); } catch { client = "unknown user"; }
            var response = request is null
                ? new PipeResponse(false, ["Empty request."])
                : await ProcessAsync(request, client, timeout.Token);
            await PipeProtocol.WriteAsync(pipe, response, timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogDebug(ex, "Pipe client failed");
        }
        catch (OperationCanceledException) { }
    }

    private async Task<PipeResponse> ProcessAsync(PipeRequest request, string client, CancellationToken ct)
    {
        switch (request.Command)
        {
            case Commands.GetSettings:
                return new PipeResponse(true, Settings: store.Current.ToView());

            case Commands.SaveSettings when request.Settings is { } update:
                var errors = update.Validate().Concat(store.ValidateCredentialBinding(update)).ToList();
                if (!NvidiaSmiLocator.IsAllowed(update.NvidiaSmiPath))
                    errors.Add($"nvidia-smi path must be 'nvidia-smi' or nvidia-smi.exe in {NvidiaSmiLocator.AllowedLocations}.");
                if (errors.Count > 0) return new PipeResponse(false, [.. errors]);
                store.Save(update);
                status.Event("info", $"Settings saved by {client}.");
                log.LogWarning("Settings changed by {User} (broker {Host}:{Port}).", client, update.Host, update.Port);
                return new PipeResponse(true, Settings: store.Current.ToView());

            case Commands.GetStatus:
                GpuReading[] readings = [];
                string? gpuError = null;
                try
                {
                    readings = await gpus.ReadAsync(store.Current.NvidiaSmiPath, TimeSpan.FromSeconds(6), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    gpuError = ex.Message;
                }
                var snapshot = status.Snapshot(readings, gpuError) with { ServiceVersion = ServiceVersion };
                if (request.IncludeHistory) snapshot = snapshot with { History = history.Snapshot() };
                return new PipeResponse(true, Status: snapshot);

            default:
                return new PipeResponse(false, [$"Unknown command '{request.Command}'."]);
        }
    }

    private static PipeSecurity BuildSecurity()
    {
        var s = new PipeSecurity();
        // The account running the service needs full control to create further pipe instances.
        using (var self = WindowsIdentity.GetCurrent())
            s.AddAccessRule(new PipeAccessRule(self.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        // Only users logged on interactively (console / RDP) — not services, batch jobs or network logons.
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        return s;
    }
}
