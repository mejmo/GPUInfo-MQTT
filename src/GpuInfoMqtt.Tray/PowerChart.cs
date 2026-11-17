using System.Drawing.Drawing2D;

namespace GpuInfoMqtt.Tray;

/// <summary>Area chart of power draw over time, one series per GPU, with a hover read-out.</summary>
internal sealed class PowerChart : PaintedControl
{
    public static readonly Color[] SeriesColors = [Theme.Accent, Theme.Purple, Theme.Teal, Theme.Warn];

    private HistoryPoint[] _points = [];
    private string[] _names = [];
    private TimeSpan _range = TimeSpan.FromMinutes(15);
    private Point? _mouse;

    public PowerChart()
    {
        BackColor = Theme.Card;
        Dock = DockStyle.Fill;
        Margin = new Padding(0, 8, 0, 0);
    }

    public void SetData(HistoryPoint[] points, string[] names)
    {
        _points = points;
        _names = names;
        Invalidate();
    }

    public void SetRange(TimeSpan range)
    {
        _range = range;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e) { _mouse = e.Location; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _mouse = null; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var start = now - (long)_range.TotalMilliseconds;
        var pts = _points.Where(p => p.T >= start - 15_000).ToArray();
        var seriesCount = pts.Length == 0 ? 0 : pts.Max(p => p.P.Length);

        var max = pts.SelectMany(p => p.P).Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).Max();
        var (tick, top) = NiceScale(max);

        var labelW = TextRenderer.MeasureText($"{top:0} W", Theme.Small).Width + (int)S(10);
        var plot = new RectangleF(labelW, S(8), Width - labelW - S(4), Height - S(8) - S(26));
        if (plot.Width < 40 || plot.Height < 30) return;

        float X(long t) => plot.Left + (float)((t - start) / (double)(now - start)) * plot.Width;
        float Y(double v) => plot.Bottom - (float)(v / top) * plot.Height;

        // Horizontal grid + y labels
        using (var gridPen = new Pen(Theme.Grid, 1f))
        {
            for (var i = 0; i <= 4; i++)
            {
                var v = tick * i;
                var y = Y(v);
                g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                TextRenderer.DrawText(g, $"{v:0.#} W", Theme.Small,
                    new Rectangle(0, (int)(y - S(10)), labelW - (int)S(10), (int)S(20)), Theme.Muted,
                    NoPad | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
        }

        // Time labels
        var stepMs = _range.TotalMinutes <= 5 ? 60_000L : _range.TotalMinutes <= 15 ? 180_000L : 600_000L;
        for (var t = (start / stepMs + 1) * stepMs; t <= now; t += stepMs)
        {
            var x = X(t);
            var text = DateTimeOffset.FromUnixTimeMilliseconds(t).ToLocalTime().ToString("HH:mm");
            var size = TextRenderer.MeasureText(text, Theme.Small);
            if (x - size.Width / 2f < plot.Left - S(4) || x + size.Width / 2f > Width) continue;
            TextRenderer.DrawText(g, text, Theme.Small, new Point((int)(x - size.Width / 2f), (int)(plot.Bottom + S(7))), Theme.Muted, NoPad);
        }

        if (pts.Length == 0)
        {
            TextRenderer.DrawText(g, "Collecting data…", Theme.Body, Rectangle.Round(plot), Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        // Series
        g.SetClip(new RectangleF(plot.Left, plot.Top - S(4), plot.Width + S(4), plot.Height + S(8)));
        for (var s = 0; s < seriesCount; s++)
        {
            var color = SeriesColors[s % SeriesColors.Length];
            var segment = new List<PointF>();
            long prevT = 0;
            foreach (var p in pts)
            {
                var v = s < p.P.Length ? p.P[s] : null;
                if (v is null || (prevT > 0 && p.T - prevT > 30_000))
                {
                    DrawSegment(g, segment, plot, color);
                    segment.Clear();
                }
                if (v is { } value) segment.Add(new PointF(X(p.T), Y(value)));
                prevT = p.T;
            }
            DrawSegment(g, segment, plot, color);
        }
        g.ResetClip();

        if (_mouse is { } m && plot.Contains(m)) DrawHover(g, pts, plot, start, now, X, Y, m);
    }

    private void DrawSegment(Graphics g, List<PointF> seg, RectangleF plot, Color color)
    {
        if (seg.Count == 0) return;
        if (seg.Count == 1)
        {
            using var dot = new SolidBrush(color);
            g.FillEllipse(dot, seg[0].X - S(2), seg[0].Y - S(2), S(4), S(4));
            return;
        }

        using (var area = new GraphicsPath())
        {
            area.AddLines(seg.ToArray());
            area.AddLine(seg[^1].X, seg[^1].Y, seg[^1].X, plot.Bottom);
            area.AddLine(seg[^1].X, plot.Bottom, seg[0].X, plot.Bottom);
            area.CloseFigure();
            using var fill = new LinearGradientBrush(
                new RectangleF(plot.Left, plot.Top - 1, plot.Width, plot.Height + 2),
                Color.FromArgb(Theme.Dark ? 95 : 70, color), Color.FromArgb(0, color), LinearGradientMode.Vertical);
            g.FillPath(fill, area);
        }

        using var pen = new Pen(color, S(2)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(pen, seg.ToArray());
    }

    private void DrawHover(Graphics g, HistoryPoint[] pts, RectangleF plot, long start, long now,
        Func<long, float> X, Func<double, float> Y, Point m)
    {
        var t = start + (long)((m.X - plot.Left) / plot.Width * (now - start));
        var nearest = pts.MinBy(p => Math.Abs(p.T - t))!;
        var x = X(nearest.T);

        using (var guide = new Pen(Theme.Blend(Theme.Muted, Theme.Card, 0.4), 1f) { DashStyle = DashStyle.Dash })
            g.DrawLine(guide, x, plot.Top, x, plot.Bottom);

        var lines = new List<(string Text, Color? Dot)>
        {
            (DateTimeOffset.FromUnixTimeMilliseconds(nearest.T).ToLocalTime().ToString("HH:mm:ss"), null),
        };
        for (var s = 0; s < nearest.P.Length; s++)
        {
            if (nearest.P[s] is not { } v) continue;
            var color = SeriesColors[s % SeriesColors.Length];
            var y = Y(v);
            using (var ring = new SolidBrush(Theme.Card)) g.FillEllipse(ring, x - S(5), y - S(5), S(10), S(10));
            using (var dot = new SolidBrush(color)) g.FillEllipse(dot, x - S(3.5f), y - S(3.5f), S(7), S(7));
            var name = nearest.P.Length > 1 && s < _names.Length ? $"{_names[s]}: " : "";
            lines.Add(($"{name}{v:0.0} W", color));
        }

        var pad = (int)S(9);
        var lineH = TextRenderer.MeasureText("Ag", Theme.Semibold).Height + (int)S(2);
        var w = lines.Max(l => TextRenderer.MeasureText(l.Text, Theme.Semibold).Width + (l.Dot is null ? 0 : (int)S(14))) + pad * 2;
        var h = lineH * lines.Count + pad * 2 - (int)S(2);
        var bx = x + S(12);
        if (bx + w > Width - S(2)) bx = x - S(12) - w;
        var by = Math.Clamp(m.Y - h / 2f, plot.Top, Math.Max(plot.Top, plot.Bottom - h));

        Theme.FillRounded(g, new RectangleF(bx, by, w, h), S(6), Theme.Tooltip, Theme.Border);
        for (var i = 0; i < lines.Count; i++)
        {
            var (text, dot) = lines[i];
            var ty = (int)by + pad + i * lineH;
            var tx = (int)bx + pad;
            if (dot is { } c)
            {
                using var b = new SolidBrush(c);
                g.FillEllipse(b, tx, ty + (lineH - S(9)) / 2, S(7), S(7));
                tx += (int)S(14);
            }
            TextRenderer.DrawText(g, text, i == 0 ? Theme.Small : Theme.Semibold, new Point(tx, ty),
                i == 0 ? Theme.Muted : Theme.Text, NoPad);
        }
    }

    private static (double Tick, double Top) NiceScale(double max)
    {
        if (max <= 0) max = 10;
        var raw = max * 1.15 / 4;
        var exp = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var f = raw / exp;
        var nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10;
        var tick = nice * exp;
        return (tick, tick * 4);
    }
}
