using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace GpuInfoMqtt;

/// <summary>Persisted configuration. The password is stored separately, DPAPI-encrypted.</summary>
public sealed record AppSettings
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "homeassistant.local";
    public int Port { get; set; } = 1883;
    public bool UseTls { get; set; }
    public bool AllowUntrustedCertificates { get; set; }
    public string Username { get; set; } = "";
    public string DiscoveryPrefix { get; set; } = "homeassistant";
    public string BaseTopic { get; set; } = "gpuinfo";
    public string DeviceName { get; set; } = Environment.MachineName;
    public int IntervalSeconds { get; set; } = 10;
    public string NvidiaSmiPath { get; set; } = "nvidia-smi";

    /// <summary>"v2:" + base64 of the password protected with DPAPI in the service account's user scope.</summary>
    public string? ProtectedPassword { get; set; }

    public SettingsView ToView() => new(
        Enabled, Host, Port, UseTls, AllowUntrustedCertificates, Username,
        !string.IsNullOrEmpty(ProtectedPassword), DiscoveryPrefix, BaseTopic, DeviceName, IntervalSeconds, NvidiaSmiPath);
}

/// <summary>
/// Settings file in %ProgramData%\GpuInfoMqtt.
/// Security:
///  - the folder and file are ACL'd to SYSTEM + Administrators only (enforced on every start),
///  - the MQTT password is encrypted with DPAPI *user* scope of the service account (LocalSystem),
///    so only processes running as that account can decrypt it — not other users, not a copy of the file.
/// </summary>
public sealed class SettingsStore
{
    private const string V2Prefix = "v2:";
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GpuInfoMqtt.v1");

    private readonly object _lock = new();
    private readonly ILogger<SettingsStore> _log;
    private AppSettings _current;
    private CancellationTokenSource _changed = new();

    /// <summary>GPUINFOMQTT_DATA overrides the folder for development runs, so they never touch the real settings.</summary>
    public static string Directory { get; } = Environment.GetEnvironmentVariable("GPUINFOMQTT_DATA") is { Length: > 0 } dev
        ? Path.GetFullPath(dev)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GpuInfoMqtt");

    public static string FilePath { get; } = Path.Combine(Directory, "settings.json");

    public SettingsStore(ILogger<SettingsStore> log)
    {
        _log = log;
        HardenStorage();
        _current = Load();
        MigrateLegacyPassword();
    }

    public AppSettings Current { get { lock (_lock) return _current with { }; } }

    /// <summary>Cancelled whenever settings are saved.</summary>
    public CancellationToken ChangeToken { get { lock (_lock) return _changed.Token; } }

    public string? GetPassword(AppSettings s)
    {
        if (string.IsNullOrEmpty(s.ProtectedPassword)) return null;
        try
        {
            return Unprotect(s.ProtectedPassword);
        }
        catch (Exception ex)
        {
            _log.LogError("Stored MQTT password could not be decrypted ({Message}); please re-enter it in the tray app.", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Changing where the password is sent (host, port, user, TLS) requires re-entering it,
    /// so a local user can't redirect the stored credential to a broker they control.
    /// </summary>
    public IEnumerable<string> ValidateCredentialBinding(SettingsUpdate u)
    {
        var cur = Current;
        if (string.IsNullOrEmpty(cur.ProtectedPassword) || u.Password is not null || u.ClearPassword) yield break;
        var destinationChanged =
            !string.Equals(cur.Host, u.Host.Trim(), StringComparison.OrdinalIgnoreCase) ||
            cur.Port != u.Port ||
            !string.Equals(cur.Username, u.Username.Trim(), StringComparison.Ordinal) ||
            (cur.UseTls && !u.UseTls) ||
            (!cur.AllowUntrustedCertificates && u.AllowUntrustedCertificates);
        if (destinationChanged)
            yield return "Re-enter the password when changing the broker host, port, username or TLS settings.";
    }

    public void Save(SettingsUpdate u)
    {
        CancellationTokenSource old;
        lock (_lock)
        {
            var protectedPassword = _current.ProtectedPassword;
            if (u.ClearPassword) protectedPassword = null;
            else if (u.Password is not null) protectedPassword = u.Password.Length == 0 ? null : Protect(u.Password);

            var next = new AppSettings
            {
                Enabled = u.Enabled,
                Host = u.Host.Trim(),
                Port = u.Port,
                UseTls = u.UseTls,
                AllowUntrustedCertificates = u.AllowUntrustedCertificates,
                Username = u.Username.Trim(),
                ProtectedPassword = protectedPassword,
                DiscoveryPrefix = u.DiscoveryPrefix.Trim(),
                BaseTopic = u.BaseTopic.Trim(),
                DeviceName = u.DeviceName.Trim(),
                IntervalSeconds = u.IntervalSeconds,
                NvidiaSmiPath = u.NvidiaSmiPath.Trim(),
            };

            Write(next);
            _current = next;
            old = _changed;
            _changed = new CancellationTokenSource();
        }
        old.Cancel();
        old.Dispose();
    }

    private static string Protect(string password) =>
        V2Prefix + Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser));

    private static string Unprotect(string stored)
    {
        var v2 = stored.StartsWith(V2Prefix, StringComparison.Ordinal);
        var data = Convert.FromBase64String(v2 ? stored[V2Prefix.Length..] : stored);
        var scope = v2 ? DataProtectionScope.CurrentUser : DataProtectionScope.LocalMachine; // v1 = machine scope (1.0.0)
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(data, Entropy, scope));
    }

    /// <summary>Re-encrypts a 1.0.0 machine-scope password with the service account's user scope.</summary>
    private void MigrateLegacyPassword()
    {
        var stored = _current.ProtectedPassword;
        if (string.IsNullOrEmpty(stored) || stored.StartsWith(V2Prefix, StringComparison.Ordinal)) return;
        try
        {
            var migrated = _current with { ProtectedPassword = Protect(Unprotect(stored)) };
            Write(migrated);
            _current = migrated;
            _log.LogInformation("Migrated stored MQTT password to service-account DPAPI scope.");
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not migrate stored password: {Message}", ex.Message);
        }
    }

    private void Write(AppSettings settings)
    {
        System.IO.Directory.CreateDirectory(Directory);
        HardenStorage();
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOpts));
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>Restricts the settings folder and file to SYSTEM and Administrators (no inheritance from ProgramData).</summary>
    private void HardenStorage()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            var dirSec = new DirectorySecurity();
            dirSec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { system, admins })
                dirSec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            dirSec.SetOwner(admins);
            new DirectoryInfo(Directory).SetAccessControl(dirSec);

            foreach (var file in System.IO.Directory.EnumerateFiles(Directory))
            {
                var fileSec = new FileSecurity();
                fileSec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                foreach (var sid in new[] { system, admins })
                    fileSec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
                fileSec.SetOwner(admins);
                new FileInfo(file).SetAccessControl(fileSec);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException)
        {
            // Expected when running unelevated for development.
            _log.LogWarning("Could not restrict access to {Dir}: {Message}", Directory, ex.Message);
        }
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            _log.LogError("Failed to read {Path}: {Message}; using defaults.", FilePath, ex.Message);
        }
        return new AppSettings();
    }
}
