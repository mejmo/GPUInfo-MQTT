using System.Drawing.Drawing2D;

namespace GpuInfoMqtt.Tray;

/// <summary>Draws a small "chip" icon in a status color, so no .ico files are needed.</summary>
internal static class TrayIcons
{
    public static readonly Color Ok = Color.FromArgb(31, 157, 85);
    public static readonly Color Busy = Color.FromArgb(217, 142, 4);
    public static readonly Color Error = Color.FromArgb(214, 69, 69);
    public static readonly Color Off = Color.FromArgb(120, 126, 140);

    private static readonly Dictionary<Color, Icon> Cache = new();

    public static Icon For(Color color)
    {
        if (Cache.TryGetValue(color, out var icon)) return icon;
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp)) Draw(g, color, new RectangleF(0, 0, 32, 32));
        icon = Icon.FromHandle(bmp.GetHicon());
        Cache[color] = icon;
        return icon;
    }

    /// <summary>Draws the chip into <paramref name="bounds"/> (designed on a 32×32 grid).</summary>
    public static void Draw(Graphics g, Color color, RectangleF bounds)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(bounds.X, bounds.Y);
        g.ScaleTransform(bounds.Width / 32f, bounds.Height / 32f);

        using var pin = new Pen(color, 2.5f);
        foreach (var p in new[] { 10f, 16f, 22f })
        {
            g.DrawLine(pin, p, 1, p, 6);
            g.DrawLine(pin, p, 26, p, 31);
            g.DrawLine(pin, 1, p, 6, p);
            g.DrawLine(pin, 26, p, 31, p);
        }
        Theme.FillRounded(g, new RectangleF(5, 5, 22, 22), 4, color);
        Theme.FillRounded(g, new RectangleF(11, 11, 10, 10), 2, Color.White);
        g.Restore(state);
    }
}
