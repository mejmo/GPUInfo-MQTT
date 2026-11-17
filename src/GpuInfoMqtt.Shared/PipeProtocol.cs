using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace GpuInfoMqtt;

/// <summary>One JSON line request, one JSON line response, per pipe connection.</summary>
public static class PipeProtocol
{
    /// <summary>Overridable for development (run a second copy without clashing with the installed service).</summary>
    public static readonly string PipeName = Environment.GetEnvironmentVariable("GPUINFOMQTT_PIPE") ?? "GpuInfoMqtt";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, Json) + "\n");
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken ct, int maxBytes = 8_000_000)
    {
        // One message per connection, so reading past the newline is harmless.
        using var ms = new MemoryStream();
        var buf = new byte[16384];
        while (true)
        {
            var n = await stream.ReadAsync(buf, ct);
            if (n == 0) break;
            var nl = Array.IndexOf(buf, (byte)'\n', 0, n);
            if (nl >= 0)
            {
                ms.Write(buf, 0, nl);
                break;
            }
            ms.Write(buf, 0, n);
            if (ms.Length > maxBytes) throw new InvalidDataException("Message too large.");
        }
        return ms.Length == 0 ? default : JsonSerializer.Deserialize<T>(ms.ToArray(), Json);
    }
}

public static class PipeClient
{
    public static async Task<PipeResponse> SendAsync(PipeRequest request, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        // Identification only: the server may learn who we are, but cannot act as us.
        await using var pipe = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(2000, timeout.Token);
        EnsureServerIsTrusted(pipe);
        await PipeProtocol.WriteAsync(pipe, request, timeout.Token);
        return await PipeProtocol.ReadAsync<PipeResponse>(pipe, timeout.Token)
               ?? throw new IOException("Empty response from service.");
    }

    /// <summary>
    /// Guards against pipe-name squatting: if the service isn't running, any local user could create
    /// a pipe with our name and capture the password typed into the tray app. The real pipe is created
    /// by the LocalSystem service, so its owner is SYSTEM or Administrators, which a normal user can't fake.
    /// </summary>
    private static void EnsureServerIsTrusted(NamedPipeClientStream pipe)
    {
        if (PipeProtocol.PipeName != "GpuInfoMqtt") return; // development override
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null ||
            !(owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)))
            throw new UnauthorizedAccessException(
                $"The GpuInfoMqtt pipe is owned by {owner?.Value ?? "nobody"}, not the service. Refusing to connect.");
    }
}
