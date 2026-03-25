using System.Windows.Threading;
using ClaudeUsage.Helpers;
using ClaudeUsage.Models;
using ClaudeUsage.Services;
using Svg;
using Wpf.Ui.Appearance;
using H.NotifyIcon.Core;
using Drawing = System.Drawing;

namespace ClaudeUsage;

public partial class App : System.Windows.Application
{
    private TrayIconWithContextMenu? _trayIcon;
    private TrayIconWithContextMenu? _weeklyTrayIcon;
    private MainWindow? _mainWindow;
    private DispatcherTimer? _refreshTimer;
    private UsageData? _lastUsageData;
    private DateTime _lastUpdated;
    private PopupMenu? _contextMenu;
    private PopupMenu? _weeklyContextMenu;
    private PopupMenuItem? _launchAtLoginItem;
    private PopupMenuItem? _showDetailsItem;
    private DateTime _lastDeactivated;

    private Drawing.Icon? _currentIcon;
    private Drawing.Icon? _weeklyIcon;

    // SVG document cache — avoids re-parsing embedded resources on every icon update
    private readonly Dictionary<string, SvgDocument?> _svgCache = new();

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
        _mainWindow.SetShowDetails(StartupHelper.GetShowDetails());
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
            if (w?.ResetsAt is not { } resetsAt) continue;
            var remaining = (resetsAt - DateTimeOffset.UtcNow).TotalSeconds;
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
        if (_svgCache.TryGetValue(fileName, out var cached))
            return cached;

        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames();
        var resourceName = resourceNames.FirstOrDefault(r => r.EndsWith(fileName));

        if (resourceName == null)
        {
            _svgCache[fileName] = null;
            return null;
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            _svgCache[fileName] = null;
            return null;
        }

        var doc = SvgDocument.Open<SvgDocument>(stream);
        _svgCache[fileName] = doc;
        return doc;
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

    private Drawing.Color GetColorForUsageElapsed(double utilizationPercent, double elapsedPercent)
    {
        var adjustedUtilization = double.Max(0, utilizationPercent - 10) / 0.9;
        var adjustedElapsed = double.Max(1, elapsedPercent);
        var ratio = adjustedUtilization / adjustedElapsed;
        if (ratio > 1.1 || adjustedUtilization > 95)
            return Drawing.Color.FromArgb(239, 68, 68); // Red — over pace
        if (ratio < 0.9)
            return Drawing.Color.FromArgb(34, 197, 94); // Green — under pace
        return Drawing.Color.FromArgb(234, 179, 8);     // Yellow — on track
    }

    private void UpdateTrayIconError()
    {
        var oldIcon = _currentIcon;
        var oldWeeklyIcon = _weeklyIcon;
        var svgDoc = LoadSvgFromResource("error.svg");
        if (svgDoc != null)
        {
            _currentIcon = CreateIconFromSvg(svgDoc, Drawing.Color.FromArgb(156, 163, 175));
            _trayIcon!.UpdateIcon(_currentIcon.Handle);
            _weeklyIcon = CreateIconFromSvg(svgDoc, Drawing.Color.FromArgb(156, 163, 175));
            _weeklyTrayIcon!.UpdateIcon(_weeklyIcon.Handle);
        }
        oldIcon?.Dispose();
        oldWeeklyIcon?.Dispose();
    }

    private void OnThemeChanged(Wpf.Ui.Appearance.ApplicationTheme currentTheme, System.Windows.Media.Color systemAccent)
    {
        // Refresh the icon with current usage data to apply new theme colors
        UpdateTrayIcon();
    }

    private void UpdateTrayIcon()
    {
        if (_lastUsageData == null) return;

        // Update session icon
        var sessionWindow = _lastUsageData.FiveHour;
        var sessionUtilPct = sessionWindow?.Utilization ?? 0;
        var sessionElapsedPct = sessionWindow?.GetElapsedPercent(5 * 3600) ?? 0;
        var sessionColor = GetColorForUsageElapsed(sessionUtilPct, sessionElapsedPct);

        var oldIcon = _currentIcon;
        _currentIcon = CreateUsageIcon((int)sessionUtilPct, sessionColor);
        _trayIcon!.UpdateIcon(_currentIcon.Handle);
        oldIcon?.Dispose();
        
        // Update weekly icon
        var weeklyWindow = _lastUsageData.SevenDay;
        var weeklyUtilPct = weeklyWindow?.Utilization ?? 0;
        var weeklyElapsedPct = weeklyWindow?.GetElapsedPercent(7 * 24 * 3600) ?? 0;
        var weeklyColor = GetColorForUsageElapsed(weeklyUtilPct, weeklyElapsedPct);

        var oldWeeklyIcon = _weeklyIcon;
        _weeklyIcon = CreateUsageIcon((int)weeklyUtilPct, weeklyColor);
        _weeklyTrayIcon!.UpdateIcon(_weeklyIcon.Handle);
        oldWeeklyIcon?.Dispose();
    }

private void CreateTrayIcon()
    {
        _currentIcon = CreateUsageIcon(0, Drawing.Color.FromArgb(156, 163, 175)); // Gray
        _weeklyIcon = CreateUsageIcon(0, Drawing.Color.FromArgb(156, 163, 175)); // Gray
        
        // Create session (5-hour) tray icon
        _trayIcon = new TrayIconWithContextMenu("ClaudeUsage.Session")
        {
            Icon = _currentIcon.Handle,
            ToolTip = "Claude Session - Loading..."
        };
        
        CreateContextMenu();
        _trayIcon.Create();
        
        _trayIcon.MessageWindow.MouseEventReceived += (s, e) =>
        {
            if (e.MouseEvent == MouseEvent.IconLeftMouseUp)
            {
                Dispatcher.Invoke(() => ShowPopup());
            }
        };
        
        // Create weekly tray icon
        _weeklyTrayIcon = new TrayIconWithContextMenu("ClaudeUsage.Weekly")
        {
            Icon = _weeklyIcon.Handle,
            ToolTip = "Claude Weekly - Loading..."
        };
        
        CreateWeeklyContextMenu();
        _weeklyTrayIcon.Create();
        
        _weeklyTrayIcon.MessageWindow.MouseEventReceived += (s, e) =>
        {
            if (e.MouseEvent == MouseEvent.IconLeftMouseUp)
            {
                Dispatcher.Invoke(() => ShowPopup());
            }
        };
    }
    
    private void CreateWeeklyContextMenu()
    {
        var refreshItem = new PopupMenuItem(LocalizationService.T("refresh_now"), async (s, e) =>
        {
            await Dispatcher.InvokeAsync(() => _ = RefreshUsageData());
        });
        
        var exitItem = new PopupMenuItem(LocalizationService.T("exit"), (s, e) =>
        {
            _trayIcon?.Remove();
            _weeklyTrayIcon?.Remove();
            Dispatcher.Invoke(() => Shutdown());
        });

        _weeklyContextMenu = new PopupMenu
        {
            Items =
            {
                refreshItem,
                new PopupMenuSeparator(),
                exitItem
            }
        };
    }

    private void CreateContextMenu()
    {
        var refreshItem = new PopupMenuItem(LocalizationService.T("refresh_now"), async (s, e) =>
        {
            await Dispatcher.InvokeAsync(() => _ = RefreshUsageData());
        });

        _launchAtLoginItem = new PopupMenuItem(LocalizationService.T("launch_at_login"), (s, e) =>
        {
            var newItem = _launchAtLoginItem!;
            newItem.Checked = !newItem.Checked;
            StartupHelper.SetLaunchAtLogin(newItem.Checked);
        })
        {
            Checked = StartupHelper.IsLaunchAtLoginEnabled()
        };

        _showDetailsItem = new PopupMenuItem(LocalizationService.T("show_details"), (s, e) =>
        {
            var newItem = _showDetailsItem!;
            newItem.Checked = !newItem.Checked;
            StartupHelper.SetShowDetails(newItem.Checked);
            Dispatcher.Invoke(() => _mainWindow?.SetShowDetails(newItem.Checked));
        })
        {
            Checked = StartupHelper.GetShowDetails()
        };

        var exitItem = new PopupMenuItem(LocalizationService.T("exit"), (s, e) =>
        {
            _trayIcon?.Remove();
            Dispatcher.Invoke(() => Shutdown());
        });

        // Language submenu
        var languageItems = new List<PopupMenuItem>();
        foreach (var (code, displayName) in LocalizationService.SupportedLanguages)
        {
            var langCode = code;
            var langItem = new PopupMenuItem(displayName, (s, e) =>
            {
                LocalizationService.SetLanguage(langCode);
                StartupHelper.SaveLanguage(langCode);
                // Rebuild menu and refresh UI with new language
                CreateContextMenu();
                Dispatcher.Invoke(() =>
                {
                    _mainWindow?.ApplyLocalization();
                    if (_lastUsageData != null)
                        _mainWindow?.UpdateUsageData(_lastUsageData, _lastUpdated);
                });
            });
            languageItems.Add(langItem);
        }

        var languageMenu = new PopupSubMenu(LocalizationService.T("language"));
        foreach (var item in languageItems)
        {
            languageMenu.Items.Add(item);
        }

        _contextMenu = new PopupMenu
        {
            Items =
            {
                refreshItem,
                _showDetailsItem,
                _launchAtLoginItem,
                languageMenu,
                new PopupMenuSeparator(),
                exitItem
            }
        };

        _trayIcon!.ContextMenu = _contextMenu;
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

        _mainWindow.UpdateUsageData(_lastUsageData, _lastUpdated);
        _mainWindow.ShowPopup();
    }

    public async Task RefreshUsageData()
    {
        if (!CredentialService.CredentialsExist())
        {
            _consecutiveErrors++;
            UpdateTrayIconError();
            _trayIcon!.UpdateToolTip($"Claude Session - {LocalizationService.T("no_credentials")}");
            _weeklyTrayIcon!.UpdateToolTip($"Claude Weekly - {LocalizationService.T("no_credentials")}");
            return;
        }

        var usage = await UsageApiService.GetUsageAsync();

        if (usage == null)
        {
            _consecutiveErrors++;
            UpdateTrayIconError();
            _trayIcon!.UpdateToolTip($"Claude Session - {LocalizationService.T("failed_to_fetch")}");
            _weeklyTrayIcon!.UpdateToolTip($"Claude Weekly - {LocalizationService.T("failed_to_fetch")}");
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

        UpdateTrayIcon();

        // Update tooltips
        var sessionPct = usage.FiveHour?.UtilizationPercent ?? 0;
        var weeklyPct = usage.SevenDay?.UtilizationPercent ?? 0;
        var sessionReset = usage.FiveHour?.TimeUntilReset ?? "N/A";
        var weeklyReset = usage.SevenDay?.TimeUntilReset ?? "N/A";

        _trayIcon!.UpdateToolTip($"Claude Session\n{LocalizationService.T("tooltip_session", sessionPct, sessionReset)}");
        _weeklyTrayIcon!.UpdateToolTip($"Claude Weekly\n{LocalizationService.T("tooltip_weekly", weeklyPct, weeklyReset)}");

        // Update popup if visible
        if (_mainWindow?.IsVisible == true)
        {
            _mainWindow.UpdateUsageData(_lastUsageData, _lastUpdated);
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        try { if (_mainWindow != null) SystemThemeWatcher.UnWatch(_mainWindow); }
        catch (InvalidOperationException) { /* window handle already destroyed */ }
        ApplicationThemeManager.Changed -= OnThemeChanged;
        _trayIcon?.Dispose();
        _weeklyTrayIcon?.Dispose();
        _currentIcon?.Dispose();
        _weeklyIcon?.Dispose();
        base.OnExit(e);
    }
}
