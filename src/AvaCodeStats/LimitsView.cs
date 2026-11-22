using System.Drawing.Drawing2D;
using AvaCodeStats.Core;

namespace AvaCodeStats;

/// <summary>
/// Every quota in the active profile as a bar: filled = share left, hatched = share used,
/// white marker = share of time left in the window. Fill to the right of the marker means
/// budget is banked; fill stopping short of it means burning faster than pace.
/// </summary>
internal sealed class LimitsView : UserControl
{
    private readonly UsageCollector _collector;
    private readonly Func<AppSettings> _settings;
    private readonly FlowLayoutPanel _list = new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
        BackColor = Theme.Background, Padding = new Padding(40, 8, 40, 24),
    };

    public event Action<SubscriptionKey>? Selected;

    public LimitsView(UsageCollector collector, Func<AppSettings> settings)
    {
        _collector = collector;
        _settings = settings;
        BackColor = Theme.Background;
        Controls.Add(_list);
        _list.Resize += (_, _) => FitWidths();
    }

    public void RefreshView()
    {
        var settings = _settings();
        var now = DateTimeOffset.UtcNow;
        var groups = _collector.Latest.Values
            .Where(s => !s.Unlimited && s.Entitlement > 0 && settings.InActiveProfile(s.ProviderId, s.AccountId))
            .GroupBy(s => (s.ProviderId, s.AccountId))
            .OrderBy(g => g.Key.ProviderId).ThenBy(g => g.Key.AccountId)
            .ToList();

        _list.SuspendLayout();
        foreach (Control c in _list.Controls) c.Dispose();
        _list.Controls.Clear();

        if (groups.Count == 0)
        {
            _list.Controls.Add(new Label
            {
                Text = _collector.Errors.Count > 0
                    ? "No limits yet: " + string.Join("; ", _collector.Errors.Values)
                    : "No limits in this profile yet. Refresh, or add subscriptions in the settings file.",
                ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(0, 24, 0, 0),
            });
        }

        var showProfile = settings.ActiveProfile == Profiles.Unified;
        foreach (var g in groups)
        {
            var provider = Core.ProviderFactory.DisplayName(g.Key.ProviderId);
            var account = g.Key.AccountId == "default" ? "" : $" · {g.Key.AccountId}";
            var profile = showProfile ? $"  ({Profiles.Label(settings.ProfileOf(g.Key.ProviderId, g.Key.AccountId))})" : "";
            _list.Controls.Add(new GroupHeader(provider + account + profile, Theme.ForProvider(g.Key.ProviderId)));
            foreach (var s in g.OrderBy(s => s.PeriodEndUtc - s.PeriodStartUtc).ThenBy(s => s.MetricName))
            {
                var card = new LimitCard(s, PaceCalculator.Calculate(s, now, settings.Thresholds), now);
                card.Click += (_, _) => Selected?.Invoke(s.Key);
                _list.Controls.Add(card);
            }
        }
        _list.ResumeLayout();
        FitWidths();
    }

    private void FitWidths()
    {
        var w = _list.ClientSize.Width - _list.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;
        foreach (Control c in _list.Controls) if (c is not Label) c.Width = Math.Max(400, w);
    }

    private sealed class GroupHeader : Control
    {
        private readonly Color _dot;

        public GroupHeader(string text, Color dot)
        {
            Text = text;
            _dot = dot;
            Height = 52;
            Margin = new Padding(0);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Theme.Background;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(_dot)) g.FillEllipse(b, 2, 26, 10, 10);
            Ui.DrawText(g, Text, Theme.Strong, Theme.Text, 20, 20);
        }
    }

    private sealed class LimitCard : Control
    {
        private readonly UsageSnapshot _s;
        private readonly PaceInfo _pace;
        private readonly DateTimeOffset _now;
        private bool _hover;

        public LimitCard(UsageSnapshot s, PaceInfo pace, DateTimeOffset now)
        {
            _s = s;
            _pace = pace;
            _now = now;
            Height = 124;
            Margin = new Padding(0, 0, 0, 12);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Background;
            var tip = new ToolTip();
            tip.SetToolTip(this, "Click for the burn chart");
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var card = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.RoundedRect(card, 12))
            using (var fill = new SolidBrush(_hover ? Theme.Selected : Theme.Panel))
            using (var border = new Pen(Theme.PanelBorder))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            var left = 1 - Math.Clamp(_s.UsedFraction, 0, 1);
            var timeLeft = 1 - _pace.ElapsedFraction;
            var status = Theme.ForStatus(_pace.Status);

            // Left column: name, % left, pace line.
            Ui.DrawText(g, _s.MetricName, Theme.Strong, Theme.Text, 22, 14);
            var pct = $"{left * 100:0}%";
            Ui.DrawText(g, pct, Theme.Big, Theme.Text, 16, 34);
            var size = g.MeasureString(pct, Theme.Big);
            Ui.DrawText(g, "left", Theme.Body, Theme.Muted, 16 + size.Width - 4, 34 + size.Height * 0.55f);
            var detailY = 34 + size.Height * 0.92f;
            var detail = _s.Entitlement == 100 && _s.ProviderId != Core.Providers.GitHubCopilot.CopilotQuotaParser.ProviderId
                ? PaceCalculator.Describe(_pace)
                : $"{_s.Used:N0} of {_s.Entitlement:N0} · {PaceCalculator.Describe(_pace)}";
            Ui.DrawText(g, detail, Theme.Small, status, 22, detailY);

            // Bar.
            var x0 = Math.Max(300f, Width * 0.28f);
            var bar = new RectangleF(x0, (Height - 40) / 2f, Width - x0 - 24, 40);
            using (var clip = Theme.RoundedRect(bar, 8))
            {
                var state = g.Save();
                g.SetClip(clip);
                using (var track = new SolidBrush(Theme.Dim(Theme.Track, 0.6)))
                    g.FillRectangle(track, bar);
                using (var hatch = new HatchBrush(HatchStyle.WideUpwardDiagonal, Theme.Dim(Theme.Muted, 0.35), Color.Transparent))
                    g.FillRectangle(hatch, bar);
                using (var fill = new SolidBrush(Theme.Dim(status, 0.55)))
                    g.FillRectangle(fill, bar.X, bar.Y, (float)(bar.Width * left), bar.Height);
                g.Restore(state);
            }

            // Pace marker: where the fill should end for an even burn.
            var mx = bar.X + (float)(bar.Width * timeLeft);
            using (var marker = new Pen(Color.White, 3f))
                g.DrawLine(marker, mx, bar.Y - 3, mx, bar.Bottom + 3);

            // Reset chip.
            var chip = $"↻ {Fmt.Countdown(_s.PeriodEndUtc - _now)}";
            var cw = Ui.Width(g, chip, Theme.Small) + 10;
            var chipRect = new RectangleF(bar.Right - cw - 8, bar.Y + 9, cw, 22);
            using (var path = Theme.RoundedRect(chipRect, 6))
            using (var b = new SolidBrush(Color.FromArgb(230, Theme.Background)))
                g.FillPath(b, path);
            Ui.DrawText(g, chip, Theme.Small, Theme.Text, chipRect.X + 5, chipRect.Y + 3);
        }
    }
}
