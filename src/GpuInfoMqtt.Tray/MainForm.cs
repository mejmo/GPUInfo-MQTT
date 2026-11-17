using System.Runtime.InteropServices;

namespace GpuInfoMqtt.Tray;

internal sealed class MainForm : Form
{
    private static readonly TimeSpan[] Ranges = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)];

    // Header
    private readonly StatusPill _pill = new();
    private readonly Label _subtitle = Ui.Muted("Connecting to the service…", Theme.Body);
    private readonly TabStrip _tabs = new("Dashboard", "Settings", "Events");
    private readonly Panel[] _pages = new Panel[3];

    // Dashboard
    private readonly Label _gpuName = new() { AutoSize = true, Font = Theme.Section, ForeColor = Theme.Text, Text = "GPU", Margin = new Padding(0, 4, 12, 10) };
    private readonly ComboBox _gpuPicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Visible = false, Width = 220, FlatStyle = FlatStyle.Flat, BackColor = Theme.Input, ForeColor = Theme.Text, Margin = new Padding(0, 2, 0, 8) };
    private readonly MetricTile _power = new("POWER", Theme.Accent);
    private readonly MetricTile _temp = new("TEMPERATURE", Theme.Ok);
    private readonly MetricTile _memory = new("MEMORY", Theme.Purple);
    private readonly MetricTile _fan = new("FAN", Theme.Teal);
    private readonly MetricTile _load = new("GPU LOAD", Theme.Accent) { Margin = Padding.Empty };
    private readonly PowerChart _chart = new();
    private readonly Label _chartStats = Ui.Muted("");
    private readonly Segmented _range = new("5 min", "15 min", "1 hour");

    // Settings
    private readonly TextBox _host = new();
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535 };
    private readonly TextBox _username = new();
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };
    private readonly Label _passwordHint = Ui.Muted("");
    private readonly ToggleSwitch _clearPassword = new("Forget stored password");
    private readonly ToggleSwitch _useTls = new("Use TLS");
    private readonly ToggleSwitch _allowUntrusted = new("Accept untrusted certificate");
    private readonly TextBox _deviceName = new();
    private readonly TextBox _discoveryPrefix = new();
    private readonly TextBox _baseTopic = new();
    private readonly NumericUpDown _interval = new() { Minimum = 2, Maximum = 3600 };
    private readonly TextBox _nvidiaSmi = new();
    private readonly ToggleSwitch _enabled = new("Publish to Home Assistant");
    private readonly Button _save = Ui.AccentButton("Save && reconnect");
    private readonly Button _revert = Ui.SecondaryButton("Revert");
    private readonly Label _saveMsg = new() { AutoSize = true, Font = Theme.Body, Margin = new Padding(6, 8, 0, 0) };

    // Events
    private readonly ListBox _events = new()
    {
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 28,
        BackColor = Theme.Card, ForeColor = Theme.Text, IntegralHeight = false, Font = Theme.Body,
    };

    private StatusSnapshot? _last;
    private bool _loaded;

    // Fallback when the service doesn't supply history (older service): build it from live polls.
    private readonly List<HistoryPoint> _localHistory = [];
    private HistoryPoint[] _history = [];
    private bool _serviceOutdated;

    public MainForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "GPU Info MQTT";
        Icon = TrayIcons.For(TrayIcons.Ok);
        BackColor = Theme.Window;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(920, 660);
        MinimumSize = new Size(780, 600);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Window };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildHeader(), 0, 0);

        var tabsHost = new Panel { Dock = DockStyle.Fill, Height = 40, Padding = new Padding(24, 0, 24, 0), Margin = Padding.Empty };
        tabsHost.Controls.Add(_tabs);
        root.Controls.Add(tabsHost, 0, 1);

        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 20), Margin = Padding.Empty };
        _pages[0] = BuildDashboard();
        _pages[1] = BuildSettings();
        _pages[2] = BuildEvents();
        foreach (var page in _pages) content.Controls.Add(page);
        root.Controls.Add(content, 0, 2);
        Controls.Add(root);

        _tabs.SelectedChanged += (_, _) => ShowPage(_tabs.Selected);
        ShowPage(0);

        _range.Select(1);
        _range.SelectedChanged += (_, _) => { _chart.SetRange(Ranges[_range.Selected]); UpdateChartStats(); };
        _gpuPicker.SelectedIndexChanged += (_, _) => RenderGpu();
        _save.Click += async (_, _) => await SaveAsync();
        _revert.Click += async (_, _) => { await LoadSettingsAsync(); ShowMessage("", false); };
        _clearPassword.CheckedChanged += (_, _) => _password.Enabled = !_clearPassword.Checked;
        _events.DrawItem += DrawEvent;
        Shown += async (_, _) => await LoadSettingsAsync();
        SetEditable(false);

        ResumeLayout(false);
        PerformLayout();
    }

    // ---------- layout ----------

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1,
            Padding = new Padding(24, 18, 24, 10), Margin = Padding.Empty,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var logo = new LogoMark { Size = new Size(40, 40), Margin = new Padding(0, 0, 14, 0), Anchor = AnchorStyles.Left };
        var titles = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
        titles.Controls.Add(new Label { Text = "GPU Info MQTT", Font = Theme.Title, AutoSize = true, ForeColor = Theme.Text, Margin = Padding.Empty });
        _subtitle.Margin = new Padding(1, 0, 0, 0);
        titles.Controls.Add(_subtitle);

        header.Controls.Add(logo, 0, 0);
        header.Controls.Add(titles, 1, 0);
        header.Controls.Add(_pill, 2, 0);
        return header;
    }

    private Panel BuildDashboard()
    {
        var page = new Panel { Dock = DockStyle.Fill };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var gpuRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        gpuRow.Controls.AddRange([_gpuName, _gpuPicker]);
        grid.Controls.Add(gpuRow, 0, 0);

        var tiles = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 5, RowCount = 1, Margin = new Padding(0, 0, 0, 14) };
        for (var i = 0; i < 5; i++) tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        tiles.Controls.AddRange([_power, _temp, _memory, _fan, _load]);
        grid.Controls.Add(tiles, 0, 1);

        var chartCard = new Card { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(18, 14, 18, 12) };
        var chartLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Card };
        chartLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        chartLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var chartHeader = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, BackColor = Theme.Card, Margin = Padding.Empty };
        chartHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        chartHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var chartTitles = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty, BackColor = Theme.Card };
        chartTitles.Controls.Add(new Label { Text = "Power consumption", Font = Theme.Section, AutoSize = true, ForeColor = Theme.Text, Margin = Padding.Empty });
        _chartStats.Margin = new Padding(1, 2, 0, 0);
        chartTitles.Controls.Add(_chartStats);
        chartHeader.Controls.Add(chartTitles, 0, 0);
        chartHeader.Controls.Add(_range, 1, 0);

        chartLayout.Controls.Add(chartHeader, 0, 0);
        chartLayout.Controls.Add(_chart, 0, 1);
        chartCard.Controls.Add(chartLayout);
        grid.Controls.Add(chartCard, 0, 2);

        page.Controls.Add(grid);
        return page;
    }

    private Panel BuildSettings()
    {
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(0, 0, 4, 0) };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _passwordHint.Margin = new Padding(1, 4, 0, 0);
        stack.Controls.Add(SettingsCard("MQTT broker", "Where Home Assistant's broker (e.g. the Mosquitto add-on) is reachable.",
            [Field("Host", _host), Field("Port", _port)],
            [Field("Username", _username), Field("Password", _password, _passwordHint)],
            [Toggles(_useTls, _allowUntrusted), Toggles(_clearPassword)]));
        stack.Controls.Add(SettingsCard("Home Assistant", "How the GPU device and its sensors are named and discovered.",
            [Field("Device name", _deviceName), Field("Discovery prefix", _discoveryPrefix)],
            [Field("Base topic", _baseTopic), new Panel { Height = 1, Margin = Padding.Empty }]));
        stack.Controls.Add(SettingsCard("Polling", "How often nvidia-smi is read and values are published.",
            [Field("Interval (seconds)", _interval), Field("nvidia-smi path", _nvidiaSmi)],
            [Toggles(_enabled), new Panel { Height = 1, Margin = Padding.Empty }]));

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 2, 0, 0) };
        actions.Controls.AddRange([_save, _revert, _saveMsg]);
        stack.Controls.Add(actions);

        page.Controls.Add(stack);
        return page;
    }

    private Panel BuildEvents()
    {
        var page = new Panel { Dock = DockStyle.Fill };
        var card = new Card { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(16, 10, 16, 10) };
        card.Controls.Add(_events);
        page.Controls.Add(card);
        return page;
    }

    private static Card SettingsCard(string title, string description, params Control[][] rows)
    {
        var card = new Card { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(20, 16, 20, 8) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Theme.Card };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var heading = new Label { Text = title, Font = Theme.Section, AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, 0, 0, 2) };
        var desc = Ui.Muted(description);
        desc.Margin = new Padding(1, 0, 0, 14);
        grid.Controls.Add(heading, 0, 0);
        grid.SetColumnSpan(heading, 2);
        grid.Controls.Add(desc, 0, 1);
        grid.SetColumnSpan(desc, 2);

        var r = 2;
        foreach (var row in rows)
        {
            for (var c = 0; c < row.Length; c++) grid.Controls.Add(row[c], c, r);
            r++;
        }
        card.Controls.Add(grid);
        return card;
    }

    private static Control Field(string caption, Control input, Control? below = null)
    {
        var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 18, 12), BackColor = Theme.Card };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var label = Ui.Muted(caption);
        label.Margin = new Padding(1, 0, 0, 5);
        t.Controls.Add(label);
        t.Controls.Add(new InputFrame(input) { Dock = DockStyle.Top });
        if (below is not null) t.Controls.Add(below);
        return t;
    }

    private static Control Toggles(params ToggleSwitch[] toggles)
    {
        var f = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 0, 8), BackColor = Theme.Card };
        f.Controls.AddRange(toggles);
        return f;
    }

    private void ShowPage(int index)
    {
        for (var i = 0; i < _pages.Length; i++) _pages[i].Visible = i == index;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Dark title bar to match the theme (Windows 10 1809+ / 11).
        var on = Theme.Dark ? 1 : 0;
        _ = DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // ---------- settings ----------

    private void SetEditable(bool on)
    {
        foreach (var c in new Control[] { _host, _port, _username, _password, _clearPassword, _useTls, _allowUntrusted,
                     _deviceName, _discoveryPrefix, _baseTopic, _interval, _nvidiaSmi, _enabled, _save, _revert })
            c.Enabled = on;
        if (on) _password.Enabled = !_clearPassword.Checked;
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            var r = await PipeClient.SendAsync(new PipeRequest(Commands.GetSettings));
            if (r.Settings is { } s) Fill(s);
        }
        catch (Exception ex)
        {
            ShowMessage("Cannot reach the GpuInfoMqtt service: " + ex.Message, error: true);
        }
    }

    private void Fill(SettingsView s)
    {
        _host.Text = s.Host;
        _port.Value = Math.Clamp(s.Port, 1, 65535);
        _username.Text = s.Username;
        _password.Text = "";
        _clearPassword.Checked = false;
        _passwordHint.Text = s.HasPassword ? "A password is stored (encrypted). Leave empty to keep it." : "No password stored.";
        _password.PlaceholderText = s.HasPassword ? "••••••••  (unchanged)" : "";
        _useTls.Checked = s.UseTls;
        _allowUntrusted.Checked = s.AllowUntrustedCertificates;
        _deviceName.Text = s.DeviceName;
        _discoveryPrefix.Text = s.DiscoveryPrefix;
        _baseTopic.Text = s.BaseTopic;
        _interval.Value = Math.Clamp(s.IntervalSeconds, 2, 3600);
        _nvidiaSmi.Text = s.NvidiaSmiPath;
        _enabled.Checked = s.Enabled;
        _loaded = true;
        SetEditable(true);
    }

    private async Task SaveAsync()
    {
        if (!_loaded) return;
        var update = new SettingsUpdate(
            _enabled.Checked, _host.Text, (int)_port.Value, _useTls.Checked, _allowUntrusted.Checked, _username.Text,
            _password.Text.Length == 0 ? null : _password.Text, _clearPassword.Checked,
            _discoveryPrefix.Text, _baseTopic.Text, _deviceName.Text, (int)_interval.Value, _nvidiaSmi.Text);

        _save.Enabled = false;
        try
        {
            var r = await PipeClient.SendAsync(new PipeRequest(Commands.SaveSettings, update));
            if (r.Ok && r.Settings is { } s)
            {
                Fill(s);
                ShowMessage("Saved. Reconnecting…", error: false);
            }
            else
            {
                ShowMessage(string.Join(" ", r.Errors ?? ["Save failed."]), error: true);
            }
        }
        catch (Exception ex)
        {
            ShowMessage("Cannot reach the GpuInfoMqtt service: " + ex.Message, error: true);
        }
        finally
        {
            _save.Enabled = _loaded;
        }
    }

    private void ShowMessage(string text, bool error)
    {
        _saveMsg.ForeColor = error ? Theme.Error : Theme.Ok;
        _saveMsg.Text = text;
    }

    // ---------- status ----------

    /// <summary>Called by the tray context on every status poll.</summary>
    public void ShowStatus(StatusSnapshot? s, string? error)
    {
        if (IsDisposed) return;
        _last = s;

        if (s is null)
        {
            _pill.Set("Service not running", Theme.Off);
            _subtitle.Text = error ?? "The GpuInfoMqtt service is not reachable.";
            RenderGpu();
            return;
        }

        if (!_loaded) _ = LoadSettingsAsync();

        var (text, color) = s.State switch
        {
            "connected" when s.GpuError is null => ("Connected", Theme.Ok),
            "connected" => ("nvidia-smi error", Theme.Error),
            "connecting" or "starting" => ("Connecting", Theme.Warn),
            "disabled" => ("Disabled", Theme.Off),
            _ => ("Error", Theme.Error),
        };
        _pill.Set(text, color);

        var meta = new List<string>();
        if (s.Message is not null) meta.Add(s.Message);
        if (s.LastPublish is { } lp) meta.Add($"last publish {lp.LocalDateTime:HH:mm:ss}");
        if (s.GpuError is not null) meta.Add("nvidia-smi: " + s.GpuError);
        _subtitle.Text = string.Join("  ·  ", meta);

        // GPU picker (only visible with more than one GPU)
        var names = s.Gpus.Select(g => $"GPU {g.Index} · {TrayContext.ShortName(g.Name)}").ToArray();
        if (!names.SequenceEqual(_gpuPicker.Items.Cast<string>()))
        {
            var sel = Math.Max(0, _gpuPicker.SelectedIndex);
            _gpuPicker.Items.Clear();
            _gpuPicker.Items.AddRange(names);
            if (names.Length > 0) _gpuPicker.SelectedIndex = Math.Min(sel, names.Length - 1);
        }
        _gpuPicker.Visible = names.Length > 1;

        RenderGpu();

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (s.Gpus.Length > 0 && (_localHistory.Count == 0 || now - _localHistory[^1].T >= 2000))
            _localHistory.Add(new HistoryPoint(now, s.Gpus.Select(g => g.Power).ToArray()));
        _localHistory.RemoveAll(p => p.T < now - 3_600_000);

        _serviceOutdated = s.ServiceVersion is null;
        _history = s.History is { Length: > 0 } h ? h : [.. _localHistory];
        _chart.SetData(_history, s.Gpus.Select(g => TrayContext.ShortName(g.Name)).ToArray());
        UpdateChartStats();

        var events = s.Events.Select(e => $"{e.Level}|{e.Time.LocalDateTime:HH:mm:ss}|{e.Text}").ToArray();
        if (!events.SequenceEqual(_events.Items.Cast<string>()))
        {
            _events.BeginUpdate();
            _events.Items.Clear();
            _events.Items.AddRange(events);
            _events.EndUpdate();
        }
    }

    private void RenderGpu()
    {
        var gpus = _last?.Gpus ?? [];
        var idx = Math.Clamp(_gpuPicker.SelectedIndex, 0, Math.Max(0, gpus.Length - 1));
        if (gpus.Length == 0)
        {
            _gpuName.Text = _last is null ? "No connection to the service" : "No NVIDIA GPU found";
            foreach (var t in new[] { _power, _temp, _memory, _fan, _load }) t.Set("—", "", "", null);
            return;
        }

        var g = gpus[idx];
        _gpuName.Text = g.Name;

        _power.Set(F(g.Power, 1), "W", g.PowerLimit is { } lim ? $"of {lim:0} W limit" : "", g.Power / g.PowerLimit);

        var tempColor = g.Temperature switch { >= 83 => Theme.Error, >= 70 => Theme.Warn, _ => Theme.Ok };
        var tempText = g.Temperature switch { null => "", >= 83 => "Hot", >= 70 => "Warm", >= 45 => "Normal", _ => "Cool" };
        _temp.Set(F(g.Temperature), "°C", tempText, g.Temperature / 100, tempColor);

        var used = g.MemoryUsed / 1024;
        var total = g.MemoryTotal / 1024;
        _memory.Set(F(used, 1), "GiB", total is { } tot ? $"of {tot:0.0} GiB · {g.MemoryPercent:0.#}%" : "", g.MemoryPercent / 100);

        _fan.Set(F(g.Fan), "%", g.Fan switch { null => "Not reported", 0 => "Fans stopped (idle)", _ => "Fan speed" }, g.Fan / 100);
        _load.Set(F(g.Utilization), "%", "GPU utilization", g.Utilization / 100);
    }

    private void UpdateChartStats()
    {
        var history = _history;
        var start = DateTimeOffset.Now.ToUnixTimeMilliseconds() - (long)Ranges[_range.Selected].TotalMilliseconds;
        var pts = history.Where(p => p.T >= start).Select(p => (p.T, W: p.P.Sum(v => v ?? 0))).ToArray();
        _chartStats.ForeColor = _serviceOutdated ? Theme.Warn : Theme.Muted;
        if (_serviceOutdated)
        {
            _chartStats.Text = "Installed service is outdated: re-run install.ps1 as administrator. Showing data since this window opened.";
            return;
        }
        if (pts.Length == 0)
        {
            _chartStats.Text = "Samples every 5 s, kept for 1 hour";
            return;
        }

        double wh = 0;
        for (var i = 1; i < pts.Length; i++)
        {
            var dtH = (pts[i].T - pts[i - 1].T) / 3_600_000.0;
            if (dtH < 30 / 3600.0) wh += (pts[i].W + pts[i - 1].W) / 2 * dtH;
        }
        var label = history.FirstOrDefault()?.P.Length > 1 ? "total " : "";
        _chartStats.Text =
            $"Now {label}{pts[^1].W:0.0} W   ·   Avg {pts.Average(p => p.W):0.0} W   ·   Peak {pts.Max(p => p.W):0.0} W   ·   Energy {wh:0.00} Wh";
    }

    private static string F(double? v, int decimals = 0) => v is { } d ? d.ToString("F" + decimals) : "—";

    private void DrawEvent(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var parts = ((string)_events.Items[e.Index]).Split('|', 3);
        var g = e.Graphics;
        using (var bg = new SolidBrush(Theme.Card)) g.FillRectangle(bg, e.Bounds);
        if (e.Index > 0)
        {
            using var line = new Pen(Theme.Grid);
            g.DrawLine(line, e.Bounds.Left, e.Bounds.Top, e.Bounds.Right, e.Bounds.Top);
        }

        var isError = parts[0] == "error";
        var dpi = DeviceDpi / 96f;
        var dot = 7 * dpi;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(isError ? Theme.Error : Theme.Accent))
            g.FillEllipse(b, e.Bounds.Left + 2 * dpi, e.Bounds.Top + (e.Bounds.Height - dot) / 2, dot, dot);

        var timeRect = new Rectangle(e.Bounds.Left + (int)(16 * dpi), e.Bounds.Top, (int)(70 * dpi), e.Bounds.Height);
        TextRenderer.DrawText(g, parts[1], Theme.Small, timeRect, Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        var textRect = new Rectangle(timeRect.Right, e.Bounds.Top, e.Bounds.Width - timeRect.Right, e.Bounds.Height);
        TextRenderer.DrawText(g, parts[2], Theme.Body, textRect, isError ? Theme.Error : Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    /// <summary>App logo drawn at any size.</summary>
    private sealed class LogoMark : Control
    {
        public LogoMark()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? Theme.Window);
            TrayIcons.Draw(e.Graphics, Theme.Accent, new RectangleF(0, 0, Width, Height));
        }
    }
}
