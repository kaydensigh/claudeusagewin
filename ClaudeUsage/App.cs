using System.Runtime.InteropServices;
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

    private TrayIconWithContextMenu? _trayIcon;
    private TrayIconWithContextMenu? _weeklyTrayIcon;
    private TrayIconWithContextMenu? _sonnetTrayIcon;
    private TrayIconWithContextMenu? _overageTrayIcon;
    // System.Threading.Timer fires on a thread-pool thread, so we capture the
    // STA SynchronizationContext at startup and use Post() to marshal the callback
    // back to the main thread where H.NotifyIcon expects to be called.
    // The timer runs in one-shot mode (period = Timeout.Infinite); after each poll,
    // AdaptivePoll reschedules it with Change() to the next computed interval.
    private Timer? _refreshTimer;
    private SynchronizationContext? _syncContext;
    private UsageData? _lastUsageData;
    private PopupMenu? _contextMenu;
    private PopupMenu? _weeklyContextMenu;
    private PopupMenuItem? _launchAtLoginItem;
    private PopupMenuItem? _showDetailsItem;

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
        _syncContext = SynchronizationContext.Current;

        // Initialize localization (saved preference or auto-detect)
        var savedLang = StartupHelper.GetSavedLanguage();
        LocalizationService.Initialize(savedLang);

        // Create the tray icon
        CreateTrayIcon();

        // Set up wake timer (one-shot; OnWake reschedules after each cycle)
        _refreshTimer = new Timer(_ =>
        {
            _syncContext?.Post(async _ =>
            {
                try { await OnWake(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Wake error: {ex.Message}"); }
            }, null);
        }, null, Timeout.Infinite, Timeout.Infinite);

        // Initial data fetch
        try
        {
            if (await RefreshUsageData())
                _lastSuccessfulRefresh = DateTimeOffset.UtcNow;
        }
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
            _lastSuccessfulRefresh = DateTimeOffset.UtcNow;
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

    private static void DrawElapsedDot(Drawing.Graphics g, int iconSize, double elapsedPct)
    {
        if (elapsedPct <= 0) return;
        var dot = new Drawing.Rectangle(new(), new(2, iconSize));
        dot.X = (int)((iconSize - 2 - dot.Width) * Math.Clamp(elapsedPct, 0, 100) / 100.0);
        dot.Y = iconSize - 2 - dot.Height;
        g.FillRectangle(Drawing.Brushes.Black, dot);
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

        DrawElapsedDot(g, size, elapsedPct);

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
                if (await RefreshUsageData())
                    _lastSuccessfulRefresh = DateTimeOffset.UtcNow;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Refresh error: {ex.Message}"); }
        });

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

    private void HandleFetchError(string localizationKey)
    {
        UpdateTrayIconError();
        var msg = LocalizationService.T(localizationKey);
        _trayIcon!.UpdateToolTip($"Claude Session - {msg}");
        _weeklyTrayIcon!.UpdateToolTip($"Claude Weekly - {msg}");
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

        _lastUsageData = usage;

        UpdateTrayIcon();

        // Update tooltips
        var sessionPct = usage.FiveHour?.UtilizationPercent ?? 0;
        var weeklyPct = usage.SevenDay?.UtilizationPercent ?? 0;
        var sessionReset = usage.FiveHour?.TimeUntilReset ?? "N/A";
        var weeklyReset = usage.SevenDay?.TimeUntilReset ?? "N/A";

        _trayIcon!.UpdateToolTip($"Claude Session\n{LocalizationService.T("tooltip_session", sessionPct, sessionReset)}");
        _weeklyTrayIcon!.UpdateToolTip($"Claude Weekly\n{LocalizationService.T("tooltip_weekly", weeklyPct, weeklyReset)}");

        // Update sonnet and overage tooltips if visible
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

        return true;
    }
}
