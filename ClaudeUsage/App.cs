using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ClaudeUsage.Helpers;
using ClaudeUsage.Models;
using ClaudeUsage.Services;
using H.NotifyIcon.Core;
using Drawing = System.Drawing;

namespace ClaudeUsage;

public class App
{
    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private TrayIconWithContextMenu? _trayIcon;
    private TrayIconWithContextMenu? _weeklyTrayIcon;
    private TrayIconWithContextMenu? _sonnetTrayIcon;
    private TrayIconWithContextMenu? _overageTrayIcon;
    // System.Threading.Timer fires on a thread-pool thread in one-shot mode
    // (period = Timeout.Infinite); after each wake, OnWake reschedules it with
    // Change() to the next computed interval.  On Windows 10/11, Shell_NotifyIcon
    // works from any thread, so no SynchronizationContext marshalling is needed.
    private Timer? _refreshTimer;
    private UsageData? _lastUsageData;
    private PopupMenu? _contextMenu;
    private PopupMenu? _weeklyContextMenu;
    private PopupMenuItem? _launchAtLoginItem;
    private PopupMenuItem? _showDetailsItem;
    private HudWindow? _hudWindow;
    private bool _displayHudError;

    private Drawing.Icon? _currentIcon;
    private Drawing.Icon? _weeklyIcon;
    private Drawing.Icon? _sonnetIcon;
    private Drawing.Icon? _overageIcon;

    // Track last icon state to avoid recreating identical icons
    private (int pct, Drawing.Color color, int elapsed) _lastSessionState;
    private (int pct, Drawing.Color color, int elapsed) _lastWeeklyState;
    private (int pct, Drawing.Color color, int elapsed) _lastSonnetState;
    private (int pct, Drawing.Color color, int elapsed) _lastOverageState;

    // Shared colors
    private static readonly Drawing.Color ColorGray = Drawing.Color.FromArgb(156, 163, 175);
    private static readonly Drawing.Color ColorRed = Drawing.Color.FromArgb(239, 68, 68);
    private static readonly Drawing.Color ColorGreen = Drawing.Color.FromArgb(34, 197, 94);
    private static readonly Drawing.Color ColorYellow = Drawing.Color.FromArgb(234, 179, 8);
    private static readonly Drawing.Color ColorPurple = Drawing.Color.FromArgb(168, 85, 247);

    // Window period durations
    private const int FiveHourSeconds = 5 * 3600;
    private const int SevenDaySeconds = 7 * 24 * 3600;

    // Wake / refresh timing
    private const int WakeInterval = 300;     // 5 min — normal wake cadence
    private const int RefreshMinAge = 240;    // 4 min — skip refresh if last success was more recent
    private const int RetryDelay = 60;        // 1 min — wake sooner after a failed refresh
    private const int ResetBuffer = 5;        // seconds to wait after a quota reset before waking
    private const int IdleThreshold = 600;    // 10 min idle before skipping refresh
    private const int MaxBackoff = 1200;      // 20 min max retry backoff

    // Shared font for icon rendering (reused across CreateUsageIcon calls)
    private static readonly Drawing.Font IconFont = new("Segoe UI Semibold", 18, Drawing.FontStyle.Regular);

    private bool _isRetryWake;
    private int _consecutiveErrors;
    private DateTimeOffset _lastSuccessfulRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFailedRefresh = DateTimeOffset.MinValue;

    public async void Start()
    {
        // Initialize localization (saved preference or auto-detect)
        var savedLang = StartupHelper.GetSavedLanguage();
        LocalizationService.Initialize(savedLang);

        // Create the tray icon
        CreateTrayIcon();

        TryInitializeHud();

        // Set up wake timer (one-shot; OnWake reschedules after each cycle)
        _refreshTimer = new Timer(async _ =>
        {
            try { await OnWake(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Wake error: {ex.Message}");
                // Reschedule so the app doesn't silently freeze
                try { ScheduleWake(RetryDelay); } catch { /* timer disposed */ }
            }
        }, null, Timeout.Infinite, Timeout.Infinite);

        // Initial data fetch
        try { await RefreshUsageData(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Initial fetch error: {ex.Message}"); }

        // Schedule first wake
        ScheduleWake(WakeInterval);
    }

    public void Shutdown()
    {
        _refreshTimer?.Dispose();
        _trayIcon?.Dispose();
        _weeklyTrayIcon?.Dispose();
        _sonnetTrayIcon?.Dispose();
        _overageTrayIcon?.Dispose();
        _currentIcon?.Dispose();
        _weeklyIcon?.Dispose();
        _sonnetIcon?.Dispose();
        _overageIcon?.Dispose();
        _hudWindow?.AllowClose();
    }

    /// <summary>Creates the optional HUD overlay; failures are logged and tray-only mode continues.</summary>
    private void TryInitializeHud()
    {
        try
        {
            var settings = HudSettingsStore.Load();
            _hudWindow = new HudWindow(settings, ShowTrayContextMenuFromHud);
            _hudWindow.ApplyStartupPosition();
            if (settings.Visible)
                _hudWindow.Show();
            else
                _hudWindow.Visibility = Visibility.Hidden;
            TryUpdateHud();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"HUD init failed: {ex.Message}");
            _hudWindow = null;
        }
    }

    /// <summary>Shows the session tray’s native popup menu at the pointer (HUD right-click).</summary>
    private void ShowTrayContextMenuFromHud(double screenX, double screenY)
    {
        var hud = _hudWindow;
        var menu = _contextMenu;
        if (menu == null || hud == null) return;
        try
        {
            void ShowMenu()
            {
                var hwnd = new WindowInteropHelper(hud).EnsureHandle();
                SetForegroundWindow(hwnd);
                hud.ReassertTopmostForShell();
                menu.Show(hwnd, (int)Math.Round(screenX), (int)Math.Round(screenY));
            }

            if (hud.Dispatcher.CheckAccess())
                ShowMenu();
            else
                hud.Dispatcher.Invoke(ShowMenu);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"HUD tray menu: {ex}");
        }
    }

    /// <summary>Shows or hides the HUD from the tray menu.</summary>
    private void ToggleHudOverlay()
    {
        if (_hudWindow == null) return;
        try
        {
            // Tray context menus can run off the WPF UI thread; window calls must be marshalled.
            var hud = _hudWindow;
            if (hud.Dispatcher.CheckAccess())
                hud.ToggleFromTray();
            else
                hud.Dispatcher.Invoke(hud.ToggleFromTray);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Toggle HUD: {ex}");
        }
    }

    private static Color ToMediaColor(Drawing.Color c) =>
        Color.FromArgb(c.A, c.R, c.G, c.B);

    /// <summary>Updates HUD labels from the same usage state as the tray icons (marshals to the WPF thread).</summary>
    private void TryUpdateHud()
    {
        if (_hudWindow == null) return;

        string sessionText;
        string weeklyText;
        Drawing.Color sessionColor;
        Drawing.Color weeklyColor;

        if (_displayHudError)
        {
            sessionText = "0";
            weeklyText = "0";
            sessionColor = weeklyColor = ColorGray;
        }
        else if (_lastUsageData == null)
        {
            sessionText = "--";
            weeklyText = "--";
            sessionColor = weeklyColor = ColorGray;
        }
        else
        {
            var sessionWindow = _lastUsageData.FiveHour;
            var sessionUtilPct = sessionWindow?.Utilization ?? 0;
            var sessionElapsedPct = sessionWindow?.GetElapsedPercent(FiveHourSeconds) ?? 0;
            sessionColor = GetColorForUsageElapsed(sessionUtilPct, sessionElapsedPct);
            sessionText = ((int)sessionUtilPct).ToString();

            var weeklyWindow = _lastUsageData.SevenDay;
            var weeklyUtilPct = weeklyWindow?.Utilization ?? 0;
            var weeklyElapsedPct = weeklyWindow?.GetElapsedPercent(SevenDaySeconds) ?? 0;
            weeklyColor = IsWeeklyQuotaUnreachable(weeklyWindow)
                ? ColorPurple
                : GetColorForUsageElapsed(weeklyUtilPct, weeklyElapsedPct);
            weeklyText = ((int)weeklyUtilPct).ToString();
        }

        var sm = ToMediaColor(sessionColor);
        var wm = ToMediaColor(weeklyColor);
        _hudWindow.Dispatcher.BeginInvoke(() =>
            _hudWindow?.SetUsageDisplay(sessionText, weeklyText, sm, wm));
    }

    private async Task OnWake()
    {
        // If session or weekly is at 100%, sleep until earliest reset + buffer
        var sleepUntil = SecondsUntilCapReset();
        if (sleepUntil.HasValue)
        {
            ScheduleWake(sleepUntil.Value);
            return;
        }

        // Screen locked — skip refresh
        if (IdleHelper.IsWorkstationLocked())
        {
            ScheduleWake(WakeInterval);
            return;
        }

        // User idle 10+ min — skip refresh
        if (IdleHelper.GetIdleSeconds() >= IdleThreshold)
        {
            ScheduleWake(WakeInterval);
            return;
        }

        // Retry wake — retry if backoff has elapsed, otherwise sleep until it does
        if (_isRetryWake)
        {
            var backoff = CalculateBackoff();
            var sinceFail = (DateTimeOffset.UtcNow - _lastFailedRefresh).TotalSeconds;
            if (sinceFail >= backoff)
            {
                await AttemptRefresh();
            }
            else
            {
                var remaining = (int)(backoff - sinceFail) + ResetBuffer;
                ScheduleWake(Math.Max(remaining, 1));
            }
            return;
        }

        // Refresh if last success was long enough ago
        var sinceLast = (DateTimeOffset.UtcNow - _lastSuccessfulRefresh).TotalSeconds;
        if (sinceLast >= RefreshMinAge)
        {
            await AttemptRefresh();
        }
        else
        {
            // Too soon — schedule wake at 5 min after last refresh
            var remaining = (int)(WakeInterval - sinceLast);
            ScheduleWake(Math.Max(remaining, 1));
        }
    }

    private async Task AttemptRefresh()
    {
        if (await RefreshUsageData())
        {
            _consecutiveErrors = 0;
            _isRetryWake = false;
            ScheduleWake(WakeInterval);
        }
        else
        {
            _consecutiveErrors++;
            _lastFailedRefresh = DateTimeOffset.UtcNow;
            _isRetryWake = true;
            ScheduleWake(RetryDelay);
        }
    }

    private int CalculateBackoff() =>
        Math.Min((int)(RetryDelay * Math.Pow(2, Math.Min(_consecutiveErrors - 1, 4))), MaxBackoff);

    private void ScheduleWake(int seconds)
    {
        _refreshTimer!.Change(seconds * 1000, Timeout.Infinite);
        RefreshTooltipTiming();
        System.Diagnostics.Debug.WriteLine($"Next wake in {seconds}s (retry={_isRetryWake})");
    }

    /// <summary>
    /// If 5-hour or weekly usage is at 100%, returns seconds until the earliest reset + buffer.
    /// </summary>
    private int? SecondsUntilCapReset()
    {
        if (_lastUsageData == null) return null;

        double? earliest = null;
        foreach (var w in new[] { _lastUsageData.FiveHour, _lastUsageData.SevenDay })
        {
            if (w is not { Utilization: >= 100 }) continue;
            if (w.ResetsAt is not { } resetsAt) continue;
            var remaining = (resetsAt - DateTimeOffset.UtcNow).TotalSeconds;
            if (remaining > 0 && (earliest == null || remaining < earliest))
                earliest = remaining;
        }

        return earliest.HasValue ? (int)earliest.Value + ResetBuffer : null;
    }

    private static void DrawBars(Drawing.Graphics g, int iconSize, int usagePercent, double elapsedPct)
    {
        var barSize = new Drawing.Size(2, iconSize / 2);
        if (usagePercent > 0)
        {
            var barX = (int)((iconSize - 2 - barSize.Width) * Math.Clamp(usagePercent, 0, 100) / 100.0);
            g.FillRectangle(Drawing.Brushes.Black, barX, 0, barSize.Width, barSize.Height);
        }
        if (elapsedPct > 0)
        {
            var barX = (int)((iconSize - 2 - barSize.Width) * Math.Clamp(elapsedPct, 0, 100) / 100.0);
            var barY = iconSize - 2 - barSize.Height;
            g.FillRectangle(Drawing.Brushes.Black, barX, barY, barSize.Width, barSize.Height);
        }
    }

    /// <summary>
    /// Renders a tray icon with the given shape, percentage text, and elapsed indicator.
    /// The drawShape delegate receives (Graphics, SolidBrush, Rectangle) and fills the icon shape.
    /// </summary>
    private Drawing.Icon RenderIcon(int percentage, Drawing.Color bgColor, double elapsedPct,
        Action<Drawing.Graphics, Drawing.SolidBrush, Drawing.Rectangle> drawShape)
    {
        const int size = 32;
        var rect = new Drawing.Rectangle(0, 0, size - 2, size - 2);

        using var bitmap = new Drawing.Bitmap(size, size);
        using var g = Drawing.Graphics.FromImage(bitmap);

        g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.HighQuality;
        g.TextRenderingHint = Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Drawing.Color.Transparent);

        using var bgBrush = new Drawing.SolidBrush(bgColor);
        drawShape(g, bgBrush, rect);

        DrawBars(g, size, percentage, elapsedPct);

        using var textBrush = new Drawing.SolidBrush(Drawing.Color.White);
        var text = percentage.ToString();
        var textSize = g.MeasureString(text, IconFont);
        var textX = (rect.Width - textSize.Width) / 2 + 1;
        var textY = (rect.Height - textSize.Height) / 2 + 1;
        g.DrawString(text, IconFont, textBrush, textX, textY);

        // Icon.FromHandle does NOT own the HICON — we must clone and destroy.
        var hIcon = bitmap.GetHicon();
        var icon = (Drawing.Icon)Drawing.Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    private Drawing.Icon CreateUsageIcon(int percentage, Drawing.Color bgColor, double elapsedPct = 0) =>
        RenderIcon(percentage, bgColor, elapsedPct, (g, brush, rect) =>
        {
            const int cornerRadius = 10;
            using var path = new Drawing.Drawing2D.GraphicsPath();
            path.AddArc(rect.X, rect.Y, cornerRadius, cornerRadius, 180, 90);
            path.AddArc(rect.Right - cornerRadius, rect.Y, cornerRadius, cornerRadius, 270, 90);
            path.AddArc(rect.Right - cornerRadius, rect.Bottom - cornerRadius, cornerRadius, cornerRadius, 0, 90);
            path.AddArc(rect.X, rect.Bottom - cornerRadius, cornerRadius, cornerRadius, 90, 90);
            path.CloseFigure();
            g.FillPath(brush, path);
        });

    private Drawing.Icon CreateWeeklyIcon(int percentage, Drawing.Color color, double elapsedPct = 0) =>
        RenderIcon(percentage, color, elapsedPct, (g, brush, rect) =>
        {
            const int cornerRadius = 1;
            const int notchRadius = 3;

            using var shape = new Drawing.Drawing2D.GraphicsPath();
            shape.AddArc(rect.X, rect.Y, cornerRadius, cornerRadius, 180, 90);
            shape.AddArc(rect.Right - cornerRadius, rect.Y, cornerRadius, cornerRadius, 270, 90);
            shape.AddArc(rect.Right - cornerRadius, rect.Bottom - cornerRadius, cornerRadius, cornerRadius, 0, 90);
            shape.AddArc(rect.X, rect.Bottom - cornerRadius, cornerRadius, cornerRadius, 90, 90);
            shape.CloseFigure();

            using var leftNotch = new Drawing.Drawing2D.GraphicsPath();
            var midY = rect.Y + rect.Height / 2;
            leftNotch.AddArc(rect.X - notchRadius, midY - notchRadius, notchRadius * 2, notchRadius * 2, 270, 180);
            leftNotch.CloseFigure();

            using var rightNotch = new Drawing.Drawing2D.GraphicsPath();
            rightNotch.AddArc(rect.Right - notchRadius, midY - notchRadius, notchRadius * 2, notchRadius * 2, 90, 180);
            rightNotch.CloseFigure();

            using var region = new Drawing.Region(shape);
            region.Exclude(leftNotch);
            region.Exclude(rightNotch);
            g.FillRegion(brush, region);
        });

    private Drawing.Icon CreateSonnetIcon(int percentage, Drawing.Color color, double elapsedPct = 0) =>
        RenderIcon(percentage, color, elapsedPct, (g, brush, rect) =>
        {
            var s = rect.Width;
            var inset = 4;
            var midY = s / 2;
            g.FillPolygon(brush, new Drawing.PointF[]
            {
                new(inset, 0), new(s - inset, 0), new(s, midY),
                new(s - inset, s), new(inset, s), new(0, midY),
            });
        });

    private Drawing.Icon CreateOverageIcon(int percentage, Drawing.Color color, double elapsedPct = 0) =>
        RenderIcon(percentage, color, elapsedPct, (g, brush, rect) =>
        {
            var s = rect.Width;
            var slant = 5;
            g.FillPolygon(brush, new Drawing.PointF[]
            {
                new(0, slant), new(s, 0), new(s, s - slant), new(0, s),
            });
        });

    private Drawing.Color GetColorForUsageElapsed(double utilizationPercent, double elapsedPercent)
    {
        var adjustedUtilization = double.Max(0, utilizationPercent - 10) / 90.0 * 100;
        var adjustedElapsed = double.Max(1, elapsedPercent);
        var ratio = adjustedUtilization / adjustedElapsed;
        if (ratio > 1.1 || adjustedUtilization > 95)
            return ColorRed;
        if (ratio < 0.9)
            return ColorGreen;
        return ColorYellow;
    }

    /// <summary>
    /// Returns true if the remaining weekly quota is unreachable given the time left.
    /// Assumes 9 five-hour sessions per week = 1 weekly quota, and 2 sessions per day max.
    /// </summary>
    private static bool IsWeeklyQuotaUnreachable(UsageWindow? window)
    {
        if (window?.ResetsAt is not { } resetsAt) return false;

        var daysLeft = (resetsAt - DateTimeOffset.UtcNow).TotalDays;
        if (daysLeft <= 0) return false;

        var remainingFraction = 1.0 - window.Utilization / 100.0;
        var consumableFraction = daysLeft * 2.0 / 9.0;

        return remainingFraction > consumableFraction;
    }

    /// <summary>
    /// Swap a tray icon only if the percentage or color has changed.
    /// </summary>
    private void SwapIcon(ref Drawing.Icon? iconField, ref (int pct, Drawing.Color color, int elapsed) lastState,
        TrayIconWithContextMenu tray, int pct, Drawing.Color color, double elapsedPct = 0,
        Func<int, Drawing.Color, double, Drawing.Icon>? iconFactory = null)
    {
        var elapsedInt = (int)elapsedPct;
        if (lastState.pct == pct && lastState.color == color && lastState.elapsed == elapsedInt && iconField != null)
            return;

        var old = iconField;
        iconField = iconFactory != null ? iconFactory(pct, color, elapsedPct) : CreateUsageIcon(pct, color, elapsedPct);
        tray.UpdateIcon(iconField.Handle);
        old?.Dispose();
        lastState = (pct, color, elapsedInt);
    }

    private void UpdateTrayIconError()
    {
        SwapIcon(ref _currentIcon, ref _lastSessionState, _trayIcon!, 0, ColorGray);
        SwapIcon(ref _weeklyIcon, ref _lastWeeklyState, _weeklyTrayIcon!, 0, ColorGray, iconFactory: CreateWeeklyIcon);
        if (_sonnetTrayIcon != null)
            SwapIcon(ref _sonnetIcon, ref _lastSonnetState, _sonnetTrayIcon, 0, ColorGray, iconFactory: CreateSonnetIcon);
        if (_overageTrayIcon != null)
            SwapIcon(ref _overageIcon, ref _lastOverageState, _overageTrayIcon, 0, ColorGray, iconFactory: CreateOverageIcon);

        TryUpdateHud();
    }

    private void UpdateTrayIcon()
    {
        if (_lastUsageData == null) return;

        // Update session icon
        var sessionWindow = _lastUsageData.FiveHour;
        var sessionUtilPct = sessionWindow?.Utilization ?? 0;
        var sessionElapsedPct = sessionWindow?.GetElapsedPercent(FiveHourSeconds) ?? 0;
        SwapIcon(ref _currentIcon, ref _lastSessionState, _trayIcon!,
            (int)sessionUtilPct, GetColorForUsageElapsed(sessionUtilPct, sessionElapsedPct), sessionElapsedPct);

        // Update weekly icon
        var weeklyWindow = _lastUsageData.SevenDay;
        var weeklyUtilPct = weeklyWindow?.Utilization ?? 0;
        var weeklyElapsedPct = weeklyWindow?.GetElapsedPercent(SevenDaySeconds) ?? 0;
        var weeklyColor = IsWeeklyQuotaUnreachable(weeklyWindow)
            ? ColorPurple
            : GetColorForUsageElapsed(weeklyUtilPct, weeklyElapsedPct);
        SwapIcon(ref _weeklyIcon, ref _lastWeeklyState, _weeklyTrayIcon!,
            (int)weeklyUtilPct, weeklyColor, weeklyElapsedPct, CreateWeeklyIcon);

        // Update sonnet icon (null check is sufficient — icon is only created when details are shown)
        if (_sonnetTrayIcon != null)
        {
            var sonnetWindow = _lastUsageData.Sonnet;
            var sonnetUtilPct = sonnetWindow?.Utilization ?? 0;
            var sonnetElapsedPct = sonnetWindow?.GetElapsedPercent(SevenDaySeconds) ?? 0;
            var sonnetColor = IsWeeklyQuotaUnreachable(sonnetWindow)
                ? ColorPurple
                : GetColorForUsageElapsed(sonnetUtilPct, sonnetElapsedPct);
            SwapIcon(ref _sonnetIcon, ref _lastSonnetState, _sonnetTrayIcon,
                (int)sonnetUtilPct, sonnetColor, sonnetElapsedPct, CreateSonnetIcon);
        }

        // Update overage icon
        if (_overageTrayIcon != null && _lastUsageData.ExtraUsage != null)
        {
            var overageUtilPct = _lastUsageData.ExtraUsage.Utilization ?? 0;
            SwapIcon(ref _overageIcon, ref _lastOverageState, _overageTrayIcon,
                (int)overageUtilPct, GetColorForUsageElapsed(overageUtilPct, 50), 50, CreateOverageIcon);
        }

        TryUpdateHud();
    }

    private void RemoveAllTrayIcons()
    {
        _trayIcon?.Remove();
        _weeklyTrayIcon?.Remove();
        _sonnetTrayIcon?.Remove();
        _overageTrayIcon?.Remove();
    }

    private void CreateTrayIcon()
    {
        _currentIcon = CreateUsageIcon(0, ColorGray);
        _weeklyIcon = CreateWeeklyIcon(0, ColorGray);

        // Create session (5-hour) tray icon
        _trayIcon = new TrayIconWithContextMenu("ClaudeUsage.Session")
        {
            Icon = _currentIcon.Handle,
            ToolTip = "Claude Session - Loading..."
        };

        CreateContextMenu();
        _trayIcon.Create();

        // Create weekly tray icon
        _weeklyTrayIcon = new TrayIconWithContextMenu("ClaudeUsage.Weekly")
        {
            Icon = _weeklyIcon.Handle,
            ToolTip = "Claude Weekly - Loading..."
        };

        CreateWeeklyContextMenu();
        _weeklyTrayIcon.Create();

        // Create sonnet and overage icons only when "Show Details" is enabled
        if (StartupHelper.GetShowDetails())
        {
            CreateSonnetTrayIcon();
            CreateOverageTrayIcon();
        }
    }

    private void CreateSonnetTrayIcon()
    {
        _sonnetIcon ??= CreateSonnetIcon(0, ColorGray);
        _sonnetTrayIcon = new TrayIconWithContextMenu("ClaudeUsage.Sonnet")
        {
            Icon = _sonnetIcon.Handle,
            ToolTip = "Claude Sonnet - Loading..."
        };

        _sonnetTrayIcon.Create();
    }

    private void CreateOverageTrayIcon()
    {
        _overageIcon ??= CreateOverageIcon(0, ColorGray);
        _overageTrayIcon = new TrayIconWithContextMenu("ClaudeUsage.Overage")
        {
            Icon = _overageIcon.Handle,
            ToolTip = "Claude Overage - Loading..."
        };
        _overageTrayIcon.Create();
    }

    private PopupMenuItem CreateRefreshMenuItem() =>
        new(LocalizationService.T("refresh_now"), async (s, e) =>
        {
            try
            {
                await RefreshUsageData();
                RefreshTooltipTiming();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Refresh error: {ex.Message}"); }
        });

    private PopupMenuItem CreateToggleHudMenuItem() =>
        new(LocalizationService.T("toggle_hud"), (s, e) => ToggleHudOverlay());

    private PopupMenuItem CreateExitMenuItem() =>
        new(LocalizationService.T("exit"), (s, e) =>
        {
            RemoveAllTrayIcons();
            Shutdown();
            Environment.Exit(0);
        });

    private void CreateWeeklyContextMenu()
    {
        _weeklyContextMenu = new PopupMenu
        {
            Items =
            {
                CreateRefreshMenuItem(),
                CreateToggleHudMenuItem(),
                new PopupMenuSeparator(),
                CreateExitMenuItem()
            }
        };

        _weeklyTrayIcon!.ContextMenu = _weeklyContextMenu;
    }

    private void CreateContextMenu()
    {
        var refreshItem = CreateRefreshMenuItem();

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
            var showDetails = newItem.Checked;
            StartupHelper.SetShowDetails(showDetails);

            if (showDetails)
            {
                // Show sonnet and overage icons
                if (_sonnetTrayIcon == null) CreateSonnetTrayIcon();
                if (_overageTrayIcon == null) CreateOverageTrayIcon();
                UpdateTrayIcon();
            }
            else
            {
                // Hide sonnet and overage icons
                _sonnetTrayIcon?.Remove();
                _sonnetTrayIcon?.Dispose();
                _sonnetTrayIcon = null;
                _overageTrayIcon?.Remove();
                _overageTrayIcon?.Dispose();
                _overageTrayIcon = null;
            }
        })
        {
            Checked = StartupHelper.GetShowDetails()
        };

        var exitItem = CreateExitMenuItem();

        // Language submenu
        var languageItems = new List<PopupMenuItem>();
        foreach (var (code, displayName) in LocalizationService.SupportedLanguages)
        {
            var langCode = code;
            var langItem = new PopupMenuItem(displayName, (s, e) =>
            {
                LocalizationService.SetLanguage(langCode);
                StartupHelper.SaveLanguage(langCode);
                // Rebuild menu with new language
                CreateContextMenu();
                CreateWeeklyContextMenu();
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
                CreateToggleHudMenuItem(),
                _showDetailsItem,
                _launchAtLoginItem,
                languageMenu,
                new PopupMenuSeparator(),
                exitItem
            }
        };

        _trayIcon!.ContextMenu = _contextMenu;
    }

    private void RefreshTooltipTiming()
    {
        if (_trayIcon == null || _weeklyTrayIcon == null) return;

        if (_lastUsageData == null)
        {
            _trayIcon.UpdateToolTip("Claude Session - Loading...");
            _weeklyTrayIcon.UpdateToolTip("Claude Weekly - Loading...");
            TryUpdateHud();
            return;
        }

        var usage = _lastUsageData;
        var sessionPct = usage.FiveHour?.UtilizationPercent ?? 0;
        var weeklyPct = usage.SevenDay?.UtilizationPercent ?? 0;
        var sessionReset = usage.FiveHour?.TimeUntilReset ?? "N/A";
        var weeklyReset = usage.SevenDay?.TimeUntilReset ?? "N/A";

        _trayIcon.UpdateToolTip($"Claude Session\n{LocalizationService.T("tooltip_session", sessionPct, sessionReset)}");
        _weeklyTrayIcon.UpdateToolTip($"Claude Weekly\n{LocalizationService.T("tooltip_weekly", weeklyPct, weeklyReset)}");

        if (_sonnetTrayIcon != null)
        {
            var sonnetPct = usage.Sonnet?.UtilizationPercent ?? 0;
            var sonnetReset = usage.Sonnet?.TimeUntilReset ?? "N/A";
            _sonnetTrayIcon.UpdateToolTip($"Claude Sonnet\n{LocalizationService.T("tooltip_session", sonnetPct, sonnetReset)}");
        }

        if (_overageTrayIcon != null && usage.ExtraUsage != null)
        {
            var overagePct = usage.ExtraUsage.UtilizationPercent;
            var overageUsed = usage.ExtraUsage.UsedDollars;
            var overageLimit = usage.ExtraUsage.LimitDollars;
            _overageTrayIcon.UpdateToolTip($"Claude Overage\n{overagePct}% | ${overageUsed:F2} / ${overageLimit:F2}");
        }

        TryUpdateHud();
    }

    private void HandleFetchError(string localizationKey)
    {
        _displayHudError = true;
        UpdateTrayIconError();
        var msg = LocalizationService.T(localizationKey);
        _trayIcon!.UpdateToolTip($"Claude Session - {msg}");
        _weeklyTrayIcon!.UpdateToolTip($"Claude Weekly - {msg}");
        _sonnetTrayIcon?.UpdateToolTip($"Claude Sonnet - {msg}");
        _overageTrayIcon?.UpdateToolTip($"Claude Overage - {msg}");
    }

    /// <summary>
    /// Fetches usage data from the API. Returns true on success.
    /// </summary>
    public async Task<bool> RefreshUsageData()
    {
        var usage = await UsageApiService.GetUsageAsync();

        if (usage == null)
        {
            HandleFetchError("failed_to_fetch");
            return false;
        }

        _displayHudError = false;
        _lastUsageData = usage;
        _lastSuccessfulRefresh = DateTimeOffset.UtcNow;

        UpdateTrayIcon();

        return true;
    }
}
