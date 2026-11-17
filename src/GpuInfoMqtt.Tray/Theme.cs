using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace GpuInfoMqtt.Tray;

/// <summary>Colors and fonts. Follows the Windows app theme (override with GPUINFOMQTT_THEME=dark|light).</summary>
internal static class Theme
{
    public static readonly bool Dark = DetectDark();

    public static readonly Color Window = Pick(0x121418, 0xF2F4F7);
    public static readonly Color Card = Pick(0x1B1E24, 0xFFFFFF);
    public static readonly Color Border = Pick(0x2A2E37, 0xE1E5EB);
    public static readonly Color Input = Pick(0x14161A, 0xF8F9FB);
    public static readonly Color Track = Pick(0x2A2E37, 0xE6E9EE);
    public static readonly Color Grid = Pick(0x252931, 0xEDEFF3);
    public static readonly Color Tooltip = Pick(0x262A33, 0xFFFFFF);
    public static readonly Color Text = Pick(0xE8EAEE, 0x1A1E26);
    public static readonly Color Muted = Pick(0x8A91A0, 0x687080);
    public static readonly Color Accent = Pick(0x5B9BFF, 0x2F6FDB);
    public static readonly Color Purple = Pick(0xA78BFA, 0x7C5CE0);
    public static readonly Color Teal = Pick(0x2DD4BF, 0x0E9C8C);
    public static readonly Color Ok = Pick(0x3DD68C, 0x1F9D55);
    public static readonly Color Warn = Pick(0xF5B73B, 0xD48A04);
    public static readonly Color Error = Pick(0xFF6B6B, 0xD64545);
    public static readonly Color Off = Pick(0x7A8190, 0x8A919E);

    public static readonly Font Body = new("Segoe UI", 9.75f);
    public static readonly Font Semibold = new("Segoe UI Semibold", 9.75f);
    public static readonly Font Small = new("Segoe UI", 8.75f);
    public static readonly Font Caption = new("Segoe UI Semibold", 8f);
    public static readonly Font Section = new("Segoe UI Semibold", 11f);
    public static readonly Font Title = new("Segoe UI Semibold", 14f);
    public static readonly Font Value = new("Segoe UI Semibold", 20f);
    public static readonly Font Unit = new("Segoe UI", 10.5f);

    private static Color Pick(int dark, int light) => Color.FromArgb(255, Color.FromArgb(Dark ? dark : light));

    public static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics g, RectangleF r, float radius, Color fill, Color? border = null)
    {
        using var path = Rounded(r, radius);
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        if (border is { } c)
        {
            using var pen = new Pen(c, 1f);
            g.DrawPath(pen, path);
        }
    }

    private static bool DetectDark()
    {
        var forced = Environment.GetEnvironmentVariable("GPUINFOMQTT_THEME");
        if (string.Equals(forced, "dark", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(forced, "light", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }
}
