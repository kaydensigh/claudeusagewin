using System.Windows.Controls;
using System.Windows.Threading;
using ClaudeUsage.Helpers;
using ClaudeUsage.Models;
using ClaudeUsage.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Svg;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace ClaudeUsage;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _notifyIcon;
    private MainWindow? _mainWindow;
    private DispatcherTimer? _refreshTimer;
    private UsageData? _lastUsageData;
    private DateTime _lastUpdated;
    private ContextMenu? _contextMenu;
    private System.Windows.Controls.MenuItem? _launchAtLoginItem;
    private DateTime _lastDeactivated;

    private Drawing.Icon? _currentIcon;

    // Adaptive polling
    private const int PollNormal = 420;       // 7 min
    private const int PollFast = 300;         // 5 min
    private const int PollIdle = 1200;        // 20 min
    private const int PollError = 60;         // 1 min after errors
    private const int PollFastExtra = 2;      // Extra fast polls after usage increase
    private const int MaxBackoff = 1200;      // 20 min max backoff
    private const int IdleThreshold = 600;    // 10 min idle before slow polling

    private int _fastPollsRemaining;
    private int _consecutiveErrors;
    private double _previousFiveHourPct = -1;

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        // Initialize localization (saved preference or auto-detect)
        var savedLang = StartupHelper.GetSavedLanguage();
        LocalizationService.Initialize(savedLang);

        // Listen for theme changes (SystemThemeWatcher in MainWindow triggers these)
        ApplicationThemeManager.Changed += OnThemeChanged;

        // Create the tray icon
        CreateTrayIcon();

        // Create the main window (hidden initially)
        _mainWindow = new MainWindow();
        _mainWindow.Deactivated += (s, args) =>
        {
            _lastDeactivated = DateTime.Now;
            _mainWindow.HideWithAnimation();
        };

        // Set up adaptive refresh timer
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(PollNormal)
        };
        _refreshTimer.Tick += async (s, args) => await AdaptivePoll();
        _refreshTimer.Start();

        // Initial data fetch
        await RefreshUsageData();
    }

    private async Task AdaptivePoll()
    {
        // Check if user is idle/locked — use slower polling
        var isIdle = IdleHelper.IsUserAway(IdleThreshold);

        if (isIdle)
        {
            _refreshTimer!.Interval = TimeSpan.FromSeconds(PollIdle);
            // Still poll, just slower
        }

        await RefreshUsageData();

        // Calculate next interval based on result
        var nextInterval = CalculatePollInterval();
        _refreshTimer!.Interval = TimeSpan.FromSeconds(nextInterval);

        System.Diagnostics.Debug.WriteLine(
            $"Adaptive poll: next in {nextInterval}s (fast={_fastPollsRemaining}, errors={_consecutiveErrors}, idle={isIdle})");
    }

    private int CalculatePollInterval()
    {
        // Error backoff
        if (_consecutiveErrors > 0)
        {
            var backoff = (int)(PollError * Math.Pow(2, Math.Min(_consecutiveErrors - 1, 4)));
            return Math.Min(backoff, MaxBackoff);
        }

        // Idle mode
        if (IdleHelper.IsUserAway(IdleThreshold))
            return PollIdle;

        // Fast polling after usage increase
        if (_fastPollsRemaining > 0)
        {
            _fastPollsRemaining--;
            return PollFast;
        }

        // Align to imminent quota reset
        var nextReset = SecondsUntilNextReset();
        if (nextReset.HasValue && nextReset.Value + 5 <= PollNormal * 1.5)
        {
            _fastPollsRemaining = PollFastExtra;
            return Math.Max((int)nextReset.Value + 5, PollFast);
        }

        return PollNormal;
    }

    private double? SecondsUntilNextReset()
    {
        if (_lastUsageData == null) return null;

        double? closest = null;

        var windows = new[] { _lastUsageData.FiveHour, _lastUsageData.SevenDay, _lastUsageData.Sonnet };
        foreach (var w in windows)
        {
            if (w == null) continue;
            if (w.ResetsAt == null) continue;
            var remaining = (w.ResetsAt.Value - DateTimeOffset.UtcNow).TotalSeconds;
            if (remaining > 0 && (closest == null || remaining < closest))
                closest = remaining;
        }

        return closest;
    }

    private Drawing.Icon CreateUsageIcon(int percentage, Drawing.Color bgColor)
    {
        // Try to load SVG icon from embedded resources
        var resourceName = GetSvgResourceName(percentage);
        var svgDoc = LoadSvgFromResource(resourceName);

        if (svgDoc != null)
        {
            return CreateIconFromSvg(svgDoc, bgColor);
        }

        // Fallback to programmatic drawing
        return CreateFallbackIcon(percentage, bgColor);
    }

    private string GetSvgResourceName(int percentage)
    {
        // Available icons: 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 95, 99, 100
        int iconValue;
        if (percentage >= 100) iconValue = 100;
        else if (percentage >= 99) iconValue = 99;
        else if (percentage >= 95) iconValue = 95;
        else if (percentage < 10) iconValue = 0; // Use 0 for 0-9% (sunglasses)
        else iconValue = (percentage / 10) * 10; // Round down to nearest 10

        return $"{iconValue}.svg";
    }

    private SvgDocument? LoadSvgFromResource(string fileName)
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames();
        var resourceName = resourceNames.FirstOrDefault(r => r.EndsWith(fileName));

        if (resourceName == null) return null;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return null;

        return SvgDocument.Open<SvgDocument>(stream);
    }

    private Drawing.Icon CreateIconFromSvg(SvgDocument svgDoc, Drawing.Color dotColor)
    {

        // Detect if dark theme
        var isDarkTheme = ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Dark;
        var frameColor = isDarkTheme ? Drawing.Color.White : Drawing.Color.FromArgb(36, 36, 36);

        // Path 0: "10" text - use frame color
        if (svgDoc.Children.Count > 0 && svgDoc.Children[0] is SvgPath textPath)
        {
            textPath.Fill = new SvgColourServer(frameColor);
        }

        // Path 1: Rectangle outline - use frame color
        if (svgDoc.Children.Count > 1 && svgDoc.Children[1] is SvgPath rectPath)
        {
            rectPath.Fill = new SvgColourServer(frameColor);
        }

        // Circle (index 2): Dot - use usage color
        if (svgDoc.Children.Count > 2 && svgDoc.Children[2] is SvgCircle dotCircle)
        {
            dotCircle.Fill = new SvgColourServer(dotColor);
        }

        // Render to bitmap
        using var bitmap = svgDoc.Draw(32, 32);
        return Drawing.Icon.FromHandle(bitmap.GetHicon());
    }

    private Drawing.Icon CreateFallbackIcon(int percentage, Drawing.Color bgColor)
    {
        const int size = 32;
        const int cornerRadius = 6;

        using var bitmap = new Drawing.Bitmap(size, size);
        using var g = Drawing.Graphics.FromImage(bitmap);

        g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.HighQuality;
        g.TextRenderingHint = Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Drawing.Color.Transparent);

        // Draw rounded rectangle background
        using var bgBrush = new Drawing.SolidBrush(bgColor);
        using var path = new Drawing.Drawing2D.GraphicsPath();
        var rect = new Drawing.Rectangle(2, 2, size - 4, size - 4);
        path.AddArc(rect.X, rect.Y, cornerRadius, cornerRadius, 180, 90);
        path.AddArc(rect.Right - cornerRadius, rect.Y, cornerRadius, cornerRadius, 270, 90);
        path.AddArc(rect.Right - cornerRadius, rect.Bottom - cornerRadius, cornerRadius, cornerRadius, 0, 90);
        path.AddArc(rect.X, rect.Bottom - cornerRadius, cornerRadius, cornerRadius, 90, 90);
        path.CloseFigure();
        g.FillPath(bgBrush, path);

        // Draw percentage number centered
        using var textFont = new Drawing.Font("Segoe UI Semibold", 10, Drawing.FontStyle.Regular);
        using var textBrush = new Drawing.SolidBrush(Drawing.Color.White);

        var text = percentage.ToString();
        var textSize = g.MeasureString(text, textFont);
        var textX = (size - textSize.Width) / 2 + 1;
        var textY = (size - textSize.Height) / 2 + 1;
        g.DrawString(text, textFont, textBrush, textX, textY);

        return Drawing.Icon.FromHandle(bitmap.GetHicon());
    }

    private Drawing.Color GetColorForUsage(double utilization)
    {
        if (utilization >= 90) return Drawing.Color.FromArgb(239, 68, 68);     // Red
        if (utilization >= 70) return Drawing.Color.FromArgb(234, 179, 8);     // Yellow
        return Drawing.Color.FromArgb(34, 197, 94);                             // Green
    }

    private void UpdateTrayIconError()
    {
        var oldIcon = _currentIcon;
        var svgDoc = LoadSvgFromResource("error.svg");
        if (svgDoc != null)
        {
            _currentIcon = CreateIconFromSvg(svgDoc, Drawing.Color.FromArgb(156, 163, 175)); // Gray dot
            _notifyIcon!.Icon = _currentIcon;
        }
        oldIcon?.Dispose();
    }

    private void OnThemeChanged(Wpf.Ui.Appearance.ApplicationTheme currentTheme, System.Windows.Media.Color systemAccent)
    {
        // Refresh the icon with current usage data to apply new theme colors
        if (_lastUsageData != null)
        {
            var sessionUtilization = _lastUsageData.FiveHour?.Utilization ?? 0;
            var maxUtilization = Math.Max(
                sessionUtilization,
                _lastUsageData.SevenDay?.Utilization ?? 0
            );
            UpdateTrayIcon((int)sessionUtilization, maxUtilization);
        }
    }

    private void UpdateTrayIcon(int percentage, double utilization)
    {
        var oldIcon = _currentIcon;
        _currentIcon = CreateUsageIcon(percentage, GetColorForUsage(utilization));
        _notifyIcon!.Icon = _currentIcon;
        oldIcon?.Dispose();
    }

    private void CreateTrayIcon()
    {
        _currentIcon = CreateUsageIcon(0, Drawing.Color.FromArgb(156, 163, 175)); // Gray
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _currentIcon,
            Visible = true,
            Text = "Claude Usage - Loading..."
        };

        // Create WPF context menu with Fluent styling
        CreateContextMenu();

        // Left-click shows the popup, right-click shows context menu
        _notifyIcon.MouseClick += (s, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                ShowPopup();
            }
            else if (e.Button == Forms.MouseButtons.Right)
            {
                ShowContextMenu();
            }
        };
    }

    private void CreateContextMenu()
    {
        _contextMenu = new ContextMenu();

        var refreshItem = new System.Windows.Controls.MenuItem
        {
            Header = LocalizationService.T("refresh_now"),
            Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowClockwise24 }
        };
        refreshItem.Click += async (s, e) => await RefreshUsageData();

        _launchAtLoginItem = new System.Windows.Controls.MenuItem
        {
            Header = LocalizationService.T("launch_at_login"),
            IsCheckable = true,
            IsChecked = StartupHelper.IsLaunchAtLoginEnabled()
        };
        _launchAtLoginItem.Click += (s, e) =>
        {
            StartupHelper.SetLaunchAtLogin(_launchAtLoginItem.IsChecked);
        };

        var exitItem = new System.Windows.Controls.MenuItem
        {
            Header = LocalizationService.T("exit"),
            Icon = new SymbolIcon { Symbol = SymbolRegular.Dismiss24 }
        };
        exitItem.Click += (s, e) =>
        {
            _notifyIcon!.Visible = false;
            Shutdown();
        };

        // Language submenu
        var languageItem = new System.Windows.Controls.MenuItem
        {
            Header = LocalizationService.T("language"),
            Icon = new SymbolIcon { Symbol = SymbolRegular.Globe24 }
        };
        foreach (var (code, displayName) in LocalizationService.SupportedLanguages)
        {
            var langCode = code;
            var langItem = new System.Windows.Controls.MenuItem { Header = displayName };
            langItem.Click += (s, e) =>
            {
                LocalizationService.SetLanguage(langCode);
                StartupHelper.SaveLanguage(langCode);
                // Rebuild menu and refresh UI with new language
                CreateContextMenu();
                _mainWindow?.ApplyLocalization();
                if (_lastUsageData != null)
                    _mainWindow?.UpdateUsageData(_lastUsageData, _lastUpdated);
            };
            languageItem.Items.Add(langItem);
        }

        _contextMenu.Items.Add(refreshItem);
        _contextMenu.Items.Add(_launchAtLoginItem);
        _contextMenu.Items.Add(languageItem);
        _contextMenu.Items.Add(new Separator());
        _contextMenu.Items.Add(exitItem);
    }

    private void ShowContextMenu()
    {
        if (_contextMenu == null) return;

        // Update launch at login state
        if (_launchAtLoginItem != null)
        {
            _launchAtLoginItem.IsChecked = StartupHelper.IsLaunchAtLoginEnabled();
        }

        _contextMenu.IsOpen = true;
    }

    private void ShowPopup()
    {
        if (_mainWindow == null) return;

        // If window was just closed by clicking tray icon, don't reopen it
        // (the click causes Deactivated which hides it, then this runs)
        if ((DateTime.Now - _lastDeactivated).TotalMilliseconds < 500)
        {
            return;
        }

        // Update the window with latest data
        _mainWindow.UpdateUsageData(_lastUsageData, _lastUpdated);

        // Position near the tray icon (bottom-right of screen)
        var workArea = System.Windows.SystemParameters.WorkArea;
        var targetLeft = workArea.Right - _mainWindow.Width - 10;

        _mainWindow.ShowWithAnimation(targetLeft, workArea.Bottom);
    }

    public async Task RefreshUsageData()
    {
        if (!CredentialService.CredentialsExist())
        {
            _consecutiveErrors++;
            UpdateTrayIconError();
            _notifyIcon!.Text = $"Claude Usage - {LocalizationService.T("no_credentials")}\n{LocalizationService.T("run_claude")}";
            return;
        }

        var usage = await UsageApiService.GetUsageAsync();

        if (usage == null)
        {
            _consecutiveErrors++;
            UpdateTrayIconError();
            _notifyIcon!.Text = $"Claude Usage - {LocalizationService.T("failed_to_fetch")}";
            return;
        }

        // Successful fetch — reset error count
        _consecutiveErrors = 0;

        // Detect usage increase for fast polling
        var currentPct = usage.FiveHour?.Utilization ?? 0;
        if (_previousFiveHourPct >= 0 && currentPct > _previousFiveHourPct)
        {
            _fastPollsRemaining = PollFastExtra + 1;
        }
        _previousFiveHourPct = currentPct;

        _lastUsageData = usage;
        _lastUpdated = DateTime.Now;

        // Update icon based on usage (utilization is already a percentage, e.g. 8.0 = 8%)
        var sessionUtilization = usage.FiveHour?.Utilization ?? 0;
        var maxUtilization = Math.Max(
            sessionUtilization,
            usage.SevenDay?.Utilization ?? 0
        );

        UpdateTrayIcon((int)sessionUtilization, maxUtilization);

        // Update tooltip
        var sessionPct = usage.FiveHour?.UtilizationPercent ?? 0;
        var weeklyPct = usage.SevenDay?.UtilizationPercent ?? 0;
        var sessionReset = usage.FiveHour?.TimeUntilReset ?? "N/A";
        var weeklyReset = usage.SevenDay?.TimeUntilReset ?? "N/A";

        _notifyIcon!.Text = $"Claude Usage\n{LocalizationService.T("tooltip_session", sessionPct, sessionReset)}\n{LocalizationService.T("tooltip_weekly", weeklyPct, weeklyReset)}";

        // Update popup if visible
        if (_mainWindow?.IsVisible == true)
        {
            _mainWindow.UpdateUsageData(_lastUsageData, _lastUpdated);
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        if (_mainWindow != null) SystemThemeWatcher.UnWatch(_mainWindow);
        ApplicationThemeManager.Changed -= OnThemeChanged;
        _notifyIcon?.Dispose();
        _currentIcon?.Dispose();
        base.OnExit(e);
    }
}
