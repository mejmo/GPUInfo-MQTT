namespace GpuInfoMqtt.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One tray icon per user session.
        using var mutex = new Mutex(true, @"Local\GpuInfoMqtt.Tray", out var isFirst);
        if (!isFirst) return;

        ApplicationConfiguration.Initialize();
        // Native controls (text boxes, scroll bars, combo boxes) follow the same light/dark theme.
        Application.SetColorMode(Theme.Dark ? SystemColorMode.Dark : SystemColorMode.Classic);
        Application.Run(new TrayContext());
    }
}
