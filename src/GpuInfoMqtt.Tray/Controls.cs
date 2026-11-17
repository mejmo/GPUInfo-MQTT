using System.Drawing.Drawing2D;

namespace GpuInfoMqtt.Tray;

internal abstract class PaintedControl : Control
{
    protected PaintedControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Window;
        ForeColor = Theme.Text;
    }

    protected float S(float v) => v * DeviceDpi / 96f;
    protected Color ParentBack => Parent?.BackColor ?? Theme.Window;

    protected const TextFormatFlags NoPad = TextFormatFlags.NoPadding;
}

/// <summary>Panel with rounded corners and a hairline border.</summary>
internal class Card : Panel
{
    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        ForeColor = Theme.Text;
        Padding = new Padding(18, 14, 18, 14);
        Margin = new Padding(0, 0, 0, 12);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Window);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.FillRounded(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 10 * DeviceDpi / 96f, BackColor, Theme.Border);
    }
}

/// <summary>Big-number tile: title, value + unit, caption and a progress bar.</summary>
internal sealed class MetricTile : PaintedControl
{
    private readonly string _title;
    private Color _color;
    private string _value = "—", _unit = "", _sub = "";
    private double? _fraction;

    public MetricTile(string title, Color color)
    {
        _title = title;
        _color = color;
        Height = 118;
        Dock = DockStyle.Fill;
        Margin = new Padding(0, 0, 12, 0);
    }

    public void Set(string value, string unit, string sub, double? fraction, Color? color = null)
    {
        _value = value;
        _unit = unit;
        _sub = sub;
        _fraction = fraction;
        if (color is { } c) _color = c;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(ParentBack);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.FillRounded(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), S(10), Theme.Card, Theme.Border);

        var p = (int)S(14);
        var titleSize = TextRenderer.MeasureText(g, _title, Theme.Caption, Size.Empty, NoPad);
        using (var b = new SolidBrush(_color))
            g.FillEllipse(b, p, p + (titleSize.Height - S(7)) / 2, S(7), S(7));
        TextRenderer.DrawText(g, _title, Theme.Caption, new Point(p + (int)S(12), p), Theme.Muted, NoPad);

        var y = p + titleSize.Height + (int)S(8);
        var vs = TextRenderer.MeasureText(g, _value, Theme.Value, Size.Empty, NoPad);
        TextRenderer.DrawText(g, _value, Theme.Value, new Point(p, y), Theme.Text, NoPad);
        if (_unit.Length > 0)
        {
            var us = TextRenderer.MeasureText(g, _unit, Theme.Unit, Size.Empty, NoPad);
            TextRenderer.DrawText(g, _unit, Theme.Unit,
                new Point(p + vs.Width + (int)S(4), y + vs.Height - us.Height - (int)S(2)), Theme.Muted, NoPad);
        }

        var barH = S(5);
        var barY = Height - p - barH;
        var track = new RectangleF(p, barY, Width - 2 * p, barH);
        Theme.FillRounded(g, track, barH / 2, Theme.Track);
        if (_fraction is double f && f > 0.001)
        {
            var w = Math.Max(barH, (float)(track.Width * Math.Clamp(f, 0, 1)));
            Theme.FillRounded(g, new RectangleF(track.X, track.Y, w, barH), barH / 2, _color);
        }

        var subH = TextRenderer.MeasureText(g, "Ag", Theme.Small, Size.Empty, NoPad).Height;
        TextRenderer.DrawText(g, _sub, Theme.Small,
            new Rectangle(p, (int)(barY - S(7) - subH), Width - 2 * p, subH), Theme.Muted, NoPad | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Rounded status badge with a colored dot.</summary>
internal sealed class StatusPill : PaintedControl
{
    private string _text = "Connecting…";
    private Color _color = Theme.Off;

    public StatusPill()
    {
        Height = 30;
        Anchor = AnchorStyles.Right;
        Margin = new Padding(12, 0, 0, 0);
    }

    public void Set(string text, Color color)
    {
        if (text == _text && color == _color && IsHandleCreated) return;
        _text = text;
        _color = color;
        Width = TextRenderer.MeasureText(_text, Theme.Semibold).Width + (int)S(34);
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Width = TextRenderer.MeasureText(_text, Theme.Semibold).Width + (int)S(34);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(ParentBack);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Theme.FillRounded(g, r, r.Height / 2, Theme.Blend(_color, ParentBack, 0.84), Theme.Blend(_color, ParentBack, 0.6));
        var d = S(8);
        using (var b = new SolidBrush(_color)) g.FillEllipse(b, S(12), (Height - d) / 2, d, d);
        TextRenderer.DrawText(g, _text, Theme.Semibold, new Rectangle((int)S(25), 0, Width, Height), Theme.Text,
            NoPad | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>Text tabs with an accent underline.</summary>
internal sealed class TabStrip : PaintedControl
{
    private readonly string[] _items;
    private int _hover = -1;

    public TabStrip(params string[] items)
    {
        _items = items;
        Height = 40;
        Dock = DockStyle.Fill;
        Cursor = Cursors.Hand;
        Margin = Padding.Empty;
    }

    public int Selected { get; private set; }
    public event EventHandler? SelectedChanged;

    public void Select(int index)
    {
        if (index == Selected) return;
        Selected = index;
        Invalidate();
        SelectedChanged?.Invoke(this, EventArgs.Empty);
    }

    private Rectangle[] ItemRects()
    {
        var rects = new Rectangle[_items.Length];
        var x = 0;
        for (var i = 0; i < _items.Length; i++)
        {
            var w = TextRenderer.MeasureText(_items[i], Theme.Semibold).Width;
            rects[i] = new Rectangle(x, 0, w, Height);
            x += w + (int)S(26);
        }
        return rects;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var line = new Pen(Theme.Border)) g.DrawLine(line, 0, Height - 1, Width, Height - 1);

        var rects = ItemRects();
        for (var i = 0; i < _items.Length; i++)
        {
            var selected = i == Selected;
            var color = selected || i == _hover ? Theme.Text : Theme.Muted;
            var text = new Rectangle(rects[i].X, 0, rects[i].Width, Height - (int)S(3));
            TextRenderer.DrawText(g, _items[i], selected ? Theme.Semibold : Theme.Body, text, color,
                NoPad | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            if (selected)
                Theme.FillRounded(g, new RectangleF(rects[i].X, Height - S(3), rects[i].Width, S(3)), S(1.5f), Theme.Accent);
        }
    }

    private int HitTest(Point p) => Array.FindIndex(ItemRects(), r => r.Contains(p));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var h = HitTest(e.Location);
        if (h != _hover) { _hover = h; Invalidate(); }
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var h = HitTest(e.Location);
        if (h >= 0) Select(h);
    }
}

/// <summary>Small segmented button group (e.g. chart range).</summary>
internal sealed class Segmented : PaintedControl
{
    private readonly string[] _items;

    public Segmented(params string[] items)
    {
        _items = items;
        Height = 28;
        Cursor = Cursors.Hand;
        Anchor = AnchorStyles.Right;
        Margin = new Padding(12, 0, 0, 0);
        BackColor = Theme.Card;
    }

    public int Selected { get; private set; }
    public event EventHandler? SelectedChanged;

    public void Select(int index)
    {
        if (index == Selected) return;
        Selected = index;
        Invalidate();
        SelectedChanged?.Invoke(this, EventArgs.Empty);
    }

    private int SegmentWidth(int i) => TextRenderer.MeasureText(_items[i], Theme.Small).Width + (int)S(22);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Width = Enumerable.Range(0, _items.Length).Sum(SegmentWidth) + (int)S(6);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(ParentBack);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.FillRounded(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), S(7), Theme.Input, Theme.Border);

        var x = (int)S(3);
        for (var i = 0; i < _items.Length; i++)
        {
            var w = SegmentWidth(i);
            var r = new Rectangle(x, (int)S(3), w, Height - (int)S(6));
            if (i == Selected) Theme.FillRounded(g, r, S(5), Theme.Accent);
            TextRenderer.DrawText(g, _items[i], i == Selected ? Theme.Semibold : Theme.Small, r,
                i == Selected ? Color.White : Theme.Muted,
                NoPad | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            x += w;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var x = (int)S(3);
        for (var i = 0; i < _items.Length; i++)
        {
            var w = SegmentWidth(i);
            if (e.X >= x && e.X < x + w) { Select(i); return; }
            x += w;
        }
    }
}

/// <summary>Rounded input box hosting a borderless TextBox / NumericUpDown.</summary>
internal sealed class InputFrame : Panel
{
    private readonly Control _inner;

    public InputFrame(Control inner)
    {
        _inner = inner;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Input;
        Height = 34;
        Padding = new Padding(10, 7, 8, 4);
        Margin = Padding.Empty;
        Cursor = Cursors.IBeam;

        inner.BackColor = Theme.Input;
        inner.ForeColor = Theme.Text;
        inner.Font = Theme.Body;
        inner.Dock = DockStyle.Fill;
        switch (inner)
        {
            case TextBox tb: tb.BorderStyle = BorderStyle.None; break;
            case NumericUpDown n: n.BorderStyle = BorderStyle.None; break;
        }
        inner.GotFocus += (_, _) => Invalidate();
        inner.LostFocus += (_, _) => Invalidate();
        inner.EnabledChanged += (_, _) => Invalidate();
        Click += (_, _) => inner.Focus();
        Controls.Add(inner);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var border = _inner.Focused ? Theme.Accent : Theme.Border;
        Theme.FillRounded(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 6 * DeviceDpi / 96f,
            _inner.Enabled ? Theme.Input : Theme.Blend(Theme.Input, Theme.Card, 0.5), border);
    }
}

/// <summary>On/off switch rendered instead of a check box.</summary>
internal sealed class ToggleSwitch : CheckBox
{
    public ToggleSwitch(string text)
    {
        Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = true;
        Cursor = Cursors.Hand;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Margin = new Padding(0, 6, 20, 6);
    }

    private float S(float v) => v * DeviceDpi / 96f;

    public override Size GetPreferredSize(Size proposedSize)
    {
        var t = TextRenderer.MeasureText(Text, Font);
        return new Size((int)S(48) + t.Width, Math.Max((int)S(26), t.Height));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var back = Parent?.BackColor ?? Theme.Card;
        g.Clear(back);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float w = S(36), h = S(20);
        var track = new RectangleF(1, (Height - h) / 2, w, h);
        var color = Checked ? Theme.Accent : Theme.Track;
        if (!Enabled) color = Theme.Blend(color, back, 0.5);
        Theme.FillRounded(g, track, h / 2, color, Checked ? null : Theme.Border);

        var k = h - S(6);
        var kx = Checked ? track.Right - k - S(3) : track.Left + S(3);
        using (var knob = new SolidBrush(Checked ? Color.White : Theme.Muted))
            g.FillEllipse(knob, kx, track.Y + S(3), k, k);

        TextRenderer.DrawText(g, Text, Font, new Rectangle((int)S(46), 0, Width - (int)S(46), Height),
            Enabled ? ForeColor : Theme.Muted, TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
    }
}

internal static class Ui
{
    public static Button AccentButton(string text)
    {
        var b = new Button
        {
            Text = text, FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White,
            Font = Theme.Semibold, AutoSize = true, Padding = new Padding(14, 4, 14, 4), Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false, Margin = new Padding(0, 0, 10, 0),
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Theme.Blend(Theme.Accent, Color.White, 0.12);
        b.FlatAppearance.MouseDownBackColor = Theme.Blend(Theme.Accent, Color.Black, 0.12);
        return b;
    }

    public static Button SecondaryButton(string text)
    {
        var b = new Button
        {
            Text = text, FlatStyle = FlatStyle.Flat, BackColor = Theme.Card, ForeColor = Theme.Text,
            Font = Theme.Body, AutoSize = true, Padding = new Padding(12, 4, 12, 4), Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false, Margin = new Padding(0, 0, 10, 0),
        };
        b.FlatAppearance.BorderColor = Theme.Border;
        b.FlatAppearance.MouseOverBackColor = Theme.Blend(Theme.Card, Theme.Text, 0.06);
        b.FlatAppearance.MouseDownBackColor = Theme.Blend(Theme.Card, Theme.Text, 0.12);
        return b;
    }

    public static Label Muted(string text, Font? font = null) => new()
    {
        Text = text, AutoSize = true, ForeColor = Theme.Muted, Font = font ?? Theme.Small, BackColor = Color.Transparent,
    };
}

/// <summary>Context menu colors matching the theme.</summary>
internal sealed class ThemedMenuColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Theme.Card;
    public override Color ImageMarginGradientBegin => Theme.Card;
    public override Color ImageMarginGradientMiddle => Theme.Card;
    public override Color ImageMarginGradientEnd => Theme.Card;
    public override Color MenuBorder => Theme.Border;
    public override Color MenuItemBorder => Theme.Blend(Theme.Accent, Theme.Card, 0.6);
    public override Color MenuItemSelected => Theme.Blend(Theme.Accent, Theme.Card, 0.8);
    public override Color MenuItemSelectedGradientBegin => MenuItemSelected;
    public override Color MenuItemSelectedGradientEnd => MenuItemSelected;
    public override Color SeparatorDark => Theme.Border;
    public override Color SeparatorLight => Theme.Border;
}
