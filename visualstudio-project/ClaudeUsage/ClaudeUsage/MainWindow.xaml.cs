using System.Windows.Media;
using System.Windows.Threading;
using ClaudeUsage.Models;
using ClaudeUsage.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ClaudeUsage;

public partial class MainWindow : FluentWindow
{
    private const int FiveHourPeriod = 18000;   // 5 hours in seconds
    private const int SevenDayPeriod = 604800;  // 7 days in seconds

    // Brushes kept for backward compat (tray icon colors in App.xaml.cs reference these indirectly)

    private const double HeightCompact = 270;
    private const double HeightDetailed = 400;

    private double _bottomEdge;
    private UsageData? _currentData;
    private readonly DispatcherTimer _countdownTimer;
    private bool _hasBeenShown;

    public MainWindow()
    {
        // Watch for OS dark/light theme changes — must be before InitializeComponent
        SystemThemeWatcher.Watch(this);

        InitializeComponent();

        // 2-second timer for live countdown and time marker updates
        _countdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _countdownTimer.Tick += (s, e) => UpdateCountdowns();

        // On first show, re-apply position after the window handle is created
        SourceInitialized += (s, e) => PositionBottomRight();

        // When window height changes, grow upward keeping bottom edge fixed
        SizeChanged += (s, e) =>
        {
            if (_bottomEdge > 0 && IsVisible)
            {
                Top = _bottomEdge - ActualHeight;
            }
        };

        // Listen for theme changes to update gauge colors
        ApplicationThemeManager.Changed += OnThemeChanged;
        UpdateGaugeTheme();

        // Apply localized strings
        ApplyLocalization();
    }

    public void ApplyLocalization()
    {
        SessionGauge.Label = LocalizationService.T("session");
        WeeklyGauge.Label = LocalizationService.T("weekly");
        SonnetLabel.Text = LocalizationService.T("sonnet_only");
        ModelSpecificLabel.Text = LocalizationService.T("model_specific");
        OverageLabel.Text = LocalizationService.T("overage");
        ExtraUsageLabel.Text = LocalizationService.T("extra_usage");
        LastUpdatedText.Text = LocalizationService.T("no_data");
    }

    public void SetShowDetails(bool show)
    {
        DetailsPanel.Visibility = show ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        Height = show ? HeightDetailed : HeightCompact;
    }

    private void OnThemeChanged(ApplicationTheme theme, System.Windows.Media.Color accent)
    {
        UpdateGaugeTheme();
    }

    private void UpdateGaugeTheme()
    {
        var isDark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        SessionGauge.IsDarkTheme = isDark;
        WeeklyGauge.IsDarkTheme = isDark;
    }

    private void PositionBottomRight()
    {
        var workArea = System.Windows.SystemParameters.WorkArea;
        Left = workArea.Right - Width - 10;
        Top = workArea.Bottom - Height - 10;
        _bottomEdge = Top + Height;
    }

    public void ShowPopup()
    {
        PositionBottomRight();
        if (!_hasBeenShown)
        {
            Show();
            _hasBeenShown = true;
        }
        else
        {
            Visibility = System.Windows.Visibility.Visible;
        }
        Activate();
        _countdownTimer.Start();
    }

    public void HideWithAnimation()
    {
        _countdownTimer.Stop();
        // Use Visibility.Hidden instead of Hide() so the visual tree stays alive
        // and the next show doesn't need a full layout + render pass.
        Visibility = System.Windows.Visibility.Hidden;
    }

    public void UpdateUsageData(UsageData? data, DateTime lastUpdated)
    {
        _currentData = data;

        if (data == null)
        {
            SessionGauge.Value = 0;
            SessionGauge.TimeElapsedPercent = null;
            SessionGauge.ResetText = LocalizationService.T("resets_in", "--");
            WeeklyGauge.Value = 0;
            WeeklyGauge.TimeElapsedPercent = null;
            WeeklyGauge.ResetText = LocalizationService.T("resets_in", "--");
            SonnetPercentText.Text = "--%";
            OverageAmountText.Text = "$--";
            OverageLimitText.Text = "";
            SetBarFill(SonnetFillBar, 0);
            SetBarFill(OverageFillBar, 0);
            SonnetResetText.Text = LocalizationService.T("resets_in", "--");
            LastUpdatedText.Text = LocalizationService.T("no_data");
            return;
        }

        // Session gauge
        var sessionPct = data.FiveHour?.UtilizationPercent ?? 0;
        SessionGauge.Value = sessionPct;
        SessionGauge.TimeElapsedPercent = data.FiveHour?.GetElapsedPercent(FiveHourPeriod);
        SessionGauge.ResetText = LocalizationService.T("resets_in", data.FiveHour?.TimeUntilReset ?? "--");

        // Weekly gauge
        var weeklyPct = data.SevenDay?.UtilizationPercent ?? 0;
        WeeklyGauge.Value = weeklyPct;
        WeeklyGauge.TimeElapsedPercent = data.SevenDay?.GetElapsedPercent(SevenDayPeriod);
        WeeklyGauge.ResetText = LocalizationService.T("resets_in", data.SevenDay?.TimeUntilReset ?? "--");

        // Sonnet Only data (seven_day_sonnet with sonnet_only fallback)
        var sonnet = data.Sonnet;
        var sonnetPct = sonnet?.UtilizationPercent ?? 0;
        SonnetPercentText.Text = $"{sonnetPct}%";
        SetBarFill(SonnetFillBar, sonnetPct);
        UpdateBarGradient(SonnetGradStart, SonnetGradEnd, sonnetPct);
        SonnetResetText.Text = LocalizationService.T("resets_in", sonnet?.TimeUntilReset ?? "--");
        SonnetPercentText.Foreground = GetBrushForPercent(sonnetPct);

        // Extra usage / overage data
        var extra = data.ExtraUsage;
        if (extra is { IsEnabled: true })
        {
            var overagePct = extra.UtilizationPercent;
            OverageAmountText.Text = $"${extra.UsedDollars:F2}";
            OverageLimitText.Text = $"of ${extra.LimitDollars:F0} limit";
            SetBarFill(OverageFillBar, overagePct);
            UpdateBarGradient(OverageGradStart, OverageGradEnd, overagePct);
            OverageAmountText.Foreground = GetBrushForPercent(overagePct);
        }
        else
        {
            OverageAmountText.Text = "$0.00";
            OverageLimitText.Text = "";
            SetBarFill(OverageFillBar, 0);
            OverageAmountText.Foreground = GetBrushForPercent(0);
        }

        // Last updated
        UpdateLastUpdatedText(lastUpdated);
    }

    private void UpdateCountdowns()
    {
        if (_currentData == null) return;

        // Update time markers and reset text live (every 2s)
        SessionGauge.TimeElapsedPercent = _currentData.FiveHour?.GetElapsedPercent(FiveHourPeriod);
        SessionGauge.ResetText = LocalizationService.T("resets_in", _currentData.FiveHour?.TimeUntilReset ?? "--");

        WeeklyGauge.TimeElapsedPercent = _currentData.SevenDay?.GetElapsedPercent(SevenDayPeriod);
        WeeklyGauge.ResetText = LocalizationService.T("resets_in", _currentData.SevenDay?.TimeUntilReset ?? "--");

        SonnetResetText.Text = LocalizationService.T("resets_in", _currentData.Sonnet?.TimeUntilReset ?? "--");
    }

    private void UpdateLastUpdatedText(DateTime lastUpdated)
    {
        var secondsAgo = (int)(DateTime.Now - lastUpdated).TotalSeconds;
        LastUpdatedText.Text = secondsAgo < 60
            ? LocalizationService.T("updated_seconds", secondsAgo)
            : LocalizationService.T("updated_minutes", (int)(DateTime.Now - lastUpdated).TotalMinutes);
    }

    private static SolidColorBrush GetBrushForPercent(int percent)
    {
        if (percent >= 90) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEB, 0x48, 0x24));
        if (percent >= 70) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xB3, 0x57));
        return new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x52, 0xD1, 0x7C));
    }

    // Track registered SizeChanged handlers to avoid leaking them
    private readonly Dictionary<System.Windows.Controls.Border, (System.Windows.Controls.Grid grid, System.Windows.SizeChangedEventHandler handler)> _barFillHandlers = new();

    private void SetBarFill(System.Windows.Controls.Border fillBar, int percent)
    {
        var parent = fillBar.Parent as System.Windows.Controls.Grid;
        if (parent == null) return;

        // Remove previous handler if one exists
        if (_barFillHandlers.TryGetValue(fillBar, out var prev))
        {
            prev.grid.SizeChanged -= prev.handler;
        }

        // Register new handler
        System.Windows.SizeChangedEventHandler handler = (s, e) =>
        {
            fillBar.Width = parent.ActualWidth * Math.Clamp(percent, 0, 100) / 100.0;
        };
        parent.SizeChanged += handler;
        _barFillHandlers[fillBar] = (parent, handler);

        fillBar.Width = parent.ActualWidth * Math.Clamp(percent, 0, 100) / 100.0;
    }

    private void UpdateBarGradient(System.Windows.Media.GradientStop gradStart, System.Windows.Media.GradientStop gradEnd, int percent)
    {
        if (percent >= 90)
        {
            gradStart.Color = System.Windows.Media.Color.FromRgb(0xFF, 0x92, 0x1F);
            gradEnd.Color = System.Windows.Media.Color.FromRgb(0xEB, 0x48, 0x24);
        }
        else if (percent >= 70)
        {
            gradStart.Color = System.Windows.Media.Color.FromRgb(0xFF, 0xD3, 0x94);
            gradEnd.Color = System.Windows.Media.Color.FromRgb(0xFF, 0xB3, 0x57);
        }
        else
        {
            gradStart.Color = System.Windows.Media.Color.FromRgb(0x52, 0xD1, 0x7C);
            gradEnd.Color = System.Windows.Media.Color.FromRgb(0x22, 0x91, 0x8B);
        }
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            HideWithAnimation();
        }
    }

    private async void RefreshButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app)
        {
            await app.RefreshUsageData();
        }
    }

    private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        HideWithAnimation();
    }

}
