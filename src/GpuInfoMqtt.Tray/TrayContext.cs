namespace GpuInfoMqtt.Tray;

internal sealed class TrayContext : ApplicationContext
{
    private const int IdleInterval = 10_000;
    private const int OpenInterval = 3_000;

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly System.Windows.Forms.Timer _timer;
    private MainForm? _form;
    private bool _refreshing;

    public TrayContext()
    {
        _statusItem = new ToolStripMenuItem("Connecting to service…") { Enabled = false };
        var openItem = new ToolStripMenuItem("Open dashboard", null, (_, _) => ShowWindow()) { Font = Theme.Semibold };

        var menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new ThemedMenuColors()) { RoundedEdges = false },
            BackColor = Theme.Card,
            ForeColor = Theme.Text,
            Font = Theme.Body,
        };
        menu.Items.AddRange([_statusItem, new ToolStripSeparator(), openItem, new ToolStripSeparator(),
            new ToolStripMenuItem("Exit tray icon", null, (_, _) => ExitThread())]);
        foreach (ToolStripItem item in menu.Items) item.ForeColor = Theme.Text;
        _statusItem.ForeColor = Theme.Muted;

        _icon = new NotifyIcon
        {
            Icon = TrayIcons.For(TrayIcons.Off),
            Text = "GPU Info MQTT",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };

        _timer = new System.Windows.Forms.Timer { Interval = IdleInterval };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    private void ShowWindow()
    {
        if (_form is { IsDisposed: false })
        {
            if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
            _form.Activate();
            return;
        }

        _form = new MainForm();
        _form.FormClosed += (_, _) => { _form = null; _timer.Interval = IdleInterval; };
        _form.Show();
        _timer.Interval = OpenInterval;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            StatusSnapshot? snapshot = null;
            string? error = null;
            try
            {
                var r = await PipeClient.SendAsync(new PipeRequest(Commands.GetStatus, IncludeHistory: _form is not null));
                snapshot = r.Status;
            }
            catch (Exception ex)
            {
                error = ex is TimeoutException or IOException or OperationCanceledException
                    ? "The GpuInfoMqtt service is not running."
                    : ex.Message;
            }

            Apply(snapshot, error);
            _form?.ShowStatus(snapshot, error);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Apply(StatusSnapshot? s, string? error)
    {
        string headline;
        Color color;
        if (s is null)
        {
            headline = "Service not running";
            color = TrayIcons.Off;
        }
        else
        {
            (headline, color) = s.State switch
            {
                "connected" when s.GpuError is null => ("Connected", TrayIcons.Ok),
                "connected" => ("Connected, nvidia-smi failing", TrayIcons.Error),
                "connecting" or "starting" => ("Connecting…", TrayIcons.Busy),
                "disabled" => ("Disabled", TrayIcons.Off),
                _ => ("Error", TrayIcons.Error),
            };
        }

        _icon.Icon = TrayIcons.For(color);
        _statusItem.Text = s?.Message is { } m ? $"{headline}: {Shorten(m, 70)}" : error ?? headline;

        var lines = new List<string> { $"GPU Info MQTT: {headline}" };
        foreach (var g in s?.Gpus ?? [])
            lines.Add($"{ShortName(g.Name)}: {Fmt(g.Temperature, "°C")} {Fmt(g.Power, " W")} fan {Fmt(g.Fan, "%")}");
        _icon.Text = Shorten(string.Join("\n", lines), 127);
    }

    internal static string ShortName(string name) => name.Replace("NVIDIA ", "").Replace("GeForce ", "").Trim();

    internal static string Fmt(double? v, string unit, int decimals = 0) =>
        v is null ? "—" : v.Value.ToString("F" + decimals) + unit;

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _form?.Close();
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
