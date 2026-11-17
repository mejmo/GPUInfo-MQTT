namespace GpuInfoMqtt;

/// <summary>
/// The service runs as LocalSystem and the path is editable by local users through the tray app,
/// so only nvidia-smi.exe from trusted, admin-only folders may be executed (prevents privilege escalation).
/// A bare name is never resolved through PATH.
/// </summary>
public static class NvidiaSmiLocator
{
    private const string ExeName = "nvidia-smi.exe";

    private static readonly string[] TrustedDirs =
    [
        Environment.SystemDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI"),
    ];

    public static string AllowedLocations => string.Join(" or ", TrustedDirs);

    private static bool IsBareName(string s) =>
        string.IsNullOrWhiteSpace(s) ||
        s.Trim().Equals("nvidia-smi", StringComparison.OrdinalIgnoreCase) ||
        s.Trim().Equals(ExeName, StringComparison.OrdinalIgnoreCase);

    public static bool IsAllowed(string configured)
    {
        if (IsBareName(configured)) return true;
        try
        {
            var full = Path.GetFullPath(configured.Trim());
            return Path.GetFileName(full).Equals(ExeName, StringComparison.OrdinalIgnoreCase) &&
                   TrustedDirs.Any(d => string.Equals(Path.GetDirectoryName(full), d, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public static string Resolve(string configured)
    {
        if (IsBareName(configured))
        {
            foreach (var dir in TrustedDirs)
            {
                var candidate = Path.Combine(dir, ExeName);
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException($"{ExeName} was not found in {AllowedLocations}.");
        }

        if (!IsAllowed(configured))
            throw new UnauthorizedAccessException($"Only {ExeName} in {AllowedLocations} may be used.");
        return Path.GetFullPath(configured.Trim());
    }
}
