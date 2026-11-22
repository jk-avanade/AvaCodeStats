using System.Runtime.InteropServices;
using AvaCodeStats.Core;

namespace AvaCodeStats;

/// <summary>Usage dashboard: profile switch, and Limits / Burn / Cost / Tokens views.</summary>
internal sealed class DashboardForm : Form
{
    public const string LimitsView = "limits";
    public const string BurnView = "burn";

    private readonly UsageCollector _collector;
    private readonly Func<AppSettings> _settings;
    private readonly Action _profileChanged;

    private readonly Segmented _profile = new(Profiles.All.Select(p => (p, Profiles.Label(p))).ToArray());
    private readonly Segmented _range = new(("24h", "Past 24h"), ("7d", "7 days"), ("30d", "30 days"), ("90d", "90 days"));
    private readonly Segmented _view = new((LimitsView, "Limits"), (BurnView, "Burn"), (AnalyticsView.CostMode, "Cost"), (AnalyticsView.TokensMode, "Tokens"));
    private readonly FlowLayoutPanel _rangeRow = new()
    {
        Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, BackColor = Theme.Background,
        Padding = new Padding(16, 0, 46, 0),
    };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small, Padding = new Padding(16, 4, 16, 6) };

    private readonly LimitsView _limits;
    private readonly BurnView _burn;
    private readonly AnalyticsView _analytics;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 60_000 };
    private Icon? _icon;

    public DashboardForm(UsageCollector collector, Func<AppSettings> settings, Action profileChanged)
    {
        _collector = collector;
        _settings = settings;
        _profileChanged = profileChanged;

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "AvaCodeStats";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1240, 900);
        MinimumSize = new Size(900, 640);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body;

        _limits = new LimitsView(collector, settings) { Dock = DockStyle.Fill };
        _burn = new BurnView(collector, settings) { Dock = DockStyle.Fill, Visible = false };
        _analytics = new AnalyticsView(collector, settings) { Dock = DockStyle.Fill, Visible = false };
        _limits.Selected += key => { _view.Select(BurnView, raise: false); ShowView(); _burn.Select(key); };

        BuildLayout();

        _profile.Select(settings().ActiveProfile, raise: false);
        _range.Select("30d", raise: false);
        _profile.Changed += (_, _) =>
        {
            var s = _settings();
            s.ActiveProfile = _profile.Value;
            s.Save();
            _profileChanged();
            RefreshCurrent();
        };
        _range.Changed += (_, _) => RefreshCurrent();
        _view.Changed += (_, _) => ShowView();

        _collector.Updated += OnUpdated;
        _tick.Tick += (_, _) => RefreshCurrent();
        _tick.Start();
        FormClosed += (_, _) => { _collector.Updated -= OnUpdated; _tick.Dispose(); RingRenderer.DestroyIcon(_icon); };

        ShowView();
    }

    public void ShowView(string view)
    {
        _view.Select(view, raise: false);
        ShowView();
    }

    private void BuildLayout()
    {
        var bar = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, BackColor = Theme.Background, Padding = new Padding(16, 12, 16, 8) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var left = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Background, Margin = new Padding(0) };
        left.Controls.Add(new Label { Text = "Usage", Font = Theme.Title, ForeColor = Theme.Text, AutoSize = true, Margin = new Padding(0, 6, 16, 0) });
        left.Controls.Add(_profile);

        var right = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Background, Margin = new Padding(0) };
        var refresh = Ui.FlatButton("⟳");
        refresh.Font = new Font("Segoe UI Symbol", 11f);
        refresh.Click += async (_, _) => { refresh.Enabled = false; await _collector.CollectAsync(); refresh.Enabled = true; };
        right.Controls.AddRange([_view, refresh]);
        _rangeRow.Controls.Add(_range);

        bar.Controls.Add(left, 0, 0);
        bar.Controls.Add(right, 2, 0);

        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background };
        host.Controls.AddRange([_limits, _burn, _analytics]);
        _status.Dock = DockStyle.Bottom;

        Controls.Add(host);
        Controls.Add(_status);
        Controls.Add(_rangeRow);
        Controls.Add(bar);
    }

    private void ShowView()
    {
        var v = _view.Value;
        _rangeRow.Visible = v is AnalyticsView.CostMode or AnalyticsView.TokensMode;
        _limits.Visible = v == LimitsView;
        _burn.Visible = v == BurnView;
        _analytics.Visible = _rangeRow.Visible;
        RefreshCurrent();
    }

    private void OnUpdated(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(RefreshCurrent);
    }

    private void RefreshCurrent()
    {
        switch (_view.Value)
        {
            case LimitsView: _limits.RefreshView(); break;
            case BurnView: _burn.RefreshView(); break;
            default: _analytics.SetMode(_view.Value, _range.Value); break;
        }
        UpdateStatus();
        UpdateIcon();
    }

    private void UpdateStatus()
    {
        var settings = _settings();
        var errors = _collector.Errors
            .Where(e => e.Key.Split(':') is [var p, var a] && settings.InActiveProfile(p, a))
            .Select(e => $"{e.Key}: {e.Value}").ToList();
        var updated = _collector.LastCollectedUtc is { } t ? $"Updated {t.ToLocalTime():HH:mm}" : "Refreshing…";
        _status.Text = errors.Count > 0 ? $"{updated} · {string.Join(" · ", errors)}" : updated;
        _status.ForeColor = errors.Count > 0 ? Theme.Amber : Theme.Muted;
    }

    private void UpdateIcon()
    {
        var s = _settings();
        var snap = _collector.PickTray(s.TrayKeys.GetValueOrDefault(s.ActiveProfile), x => s.InActiveProfile(x.ProviderId, x.AccountId));
        var pace = snap is null ? null : PaceCalculator.Calculate(snap, DateTimeOffset.UtcNow, s.Thresholds);
        var next = RingRenderer.CreateIcon(32, snap?.UsedFraction ?? 0, pace?.PaceFraction ?? 0, pace?.Status ?? PaceStatus.Unknown);
        Icon = next;
        RingRenderer.DestroyIcon(_icon);
        _icon = next;
        Text = $"AvaCodeStats · {Profiles.Label(s.ActiveProfile)}";
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var on = 1;
        _ = DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
