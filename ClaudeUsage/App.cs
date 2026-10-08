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
    private TrayIconWithContextMenu? _modelTrayIcon;
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

    private Drawing.Icon? _currentIcon;
    private Drawing.Icon? _weeklyIcon;
    private Drawing.Icon? _modelIcon;
    private Drawing.Icon? _overageIcon;

    // Track last icon state to avoid recreating identical icons
    private (int pct, Drawing.Color color, int elapsed) _lastSessionState;
    private (int pct, Drawing.Color color, int elapsed) _lastWeeklyState;
    private (int pct, Drawing.Color color, int elapsed) _lastModelState;
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
        _modelTrayIcon?.Dispose();
        _overageTrayIcon?.Dispose();
        _currentIcon?.Dispose();
        _weeklyIcon?.Dispose();
        _modelIcon?.Dispose();
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

    private Drawing.Icon CreateModelIcon(int percentage, Drawing.Color color, double elapsedPct = 0) =>
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

    private static Drawing.Color LerpColor(Drawing.Color a, Drawing.Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Drawing.Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    private Drawing.Color GetColorForUsageElapsed(double utilizationPercent, double elapsedPercent, bool allowPurple = true)
    {
        var util = Math.Clamp(utilizationPercent, 0, 100);
        var elapsed = Math.Clamp(elapsedPercent, 0, 100);

        if (util >= elapsed)
        {
            var midpoint = (elapsed + 100) / 2;
            if (util <= midpoint)
                return midpoint == elapsed ? ColorGreen : LerpColor(ColorGreen, ColorYellow, (util - elapsed) / (midpoint - elapsed));
            return midpoint == 100 ? ColorYellow : LerpColor(ColorYellow, ColorRed, (util - midpoint) / (100 - midpoint));
        }

        if (!allowPurple)
            return ColorGreen;

        // Purple means the remaining quota is unlikely to be consumed before reset.
        var purpleThreshold = 100 - (100 - elapsed) * 1.4;
        if (util <= purpleThreshold)
            return ColorPurple;
        return LerpColor(ColorPurple, ColorGreen, (util - purpleThreshold) / (elapsed - purpleThreshold));
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
        if (_modelTrayIcon != null)
            SwapIcon(ref _modelIcon, ref _lastModelState, _modelTrayIcon, 0, ColorGray, iconFactory: CreateModelIcon);
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
        SwapIcon(ref _weeklyIcon, ref _lastWeeklyState, _weeklyTrayIcon!,
            (int)weeklyUtilPct, GetColorForUsageElapsed(weeklyUtilPct, weeklyElapsedPct), weeklyElapsedPct, CreateWeeklyIcon);

        // Update model-scoped weekly icon (null check is sufficient — icon is only created when details are shown)
        if (_modelTrayIcon != null)
        {
            var modelWindow = _lastUsageData.ModelWeekly?.Window;
            if (modelWindow == null)
            {
                SwapIcon(ref _modelIcon, ref _lastModelState, _modelTrayIcon, 0, ColorGray, iconFactory: CreateModelIcon);
            }
            else
            {
                var modelUtilPct = modelWindow.Utilization;
                var modelElapsedPct = modelWindow.GetElapsedPercent(SevenDaySeconds) ?? 0;
                SwapIcon(ref _modelIcon, ref _lastModelState, _modelTrayIcon,
                    (int)modelUtilPct, GetColorForUsageElapsed(modelUtilPct, modelElapsedPct), modelElapsedPct, CreateModelIcon);
            }
        }

        // Update overage icon
        if (_overageTrayIcon != null && _lastUsageData.ExtraUsage != null)
        {
            var overageUtilPct = _lastUsageData.ExtraUsage.Utilization ?? 0;
            SwapIcon(ref _overageIcon, ref _lastOverageState, _overageTrayIcon,
                (int)overageUtilPct, GetColorForUsageElapsed(overageUtilPct, 50, allowPurple: false), 50, CreateOverageIcon);
        }
    }

    private void RemoveAllTrayIcons()
    {
        _trayIcon?.Remove();
        _weeklyTrayIcon?.Remove();
        _modelTrayIcon?.Remove();
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

        // Create model and overage icons only when "Show Details" is enabled
        if (StartupHelper.GetShowDetails())
        {
            CreateModelTrayIcon();
            CreateOverageTrayIcon();
        }
    }

    private void CreateModelTrayIcon()
    {
        _modelIcon ??= CreateModelIcon(0, ColorGray);
        // Name kept from the Sonnet era: the tray icon GUID derives from it, and
        // changing it would reset the user's taskbar visibility setting.
        _modelTrayIcon = new TrayIconWithContextMenu("ClaudeUsage.Sonnet")
        {
            Icon = _modelIcon.Handle,
            ToolTip = "Claude Model - Loading..."
        };

        _modelTrayIcon.Create();
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
                // Show model and overage icons
                if (_modelTrayIcon == null) CreateModelTrayIcon();
                if (_overageTrayIcon == null) CreateOverageTrayIcon();
                UpdateTrayIcon();
            }
            else
            {
                // Hide model and overage icons
                _modelTrayIcon?.Remove();
                _modelTrayIcon?.Dispose();
                _modelTrayIcon = null;
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

    private void RefreshTooltipTiming()
    {
        if (_trayIcon == null || _weeklyTrayIcon == null) return;

        if (_lastUsageData == null)
        {
            _trayIcon.UpdateToolTip("Claude Session - Loading...");
            _weeklyTrayIcon.UpdateToolTip("Claude Weekly - Loading...");
            return;
        }

        var usage = _lastUsageData;
        var sessionPct = usage.FiveHour?.UtilizationPercent ?? 0;
        var weeklyPct = usage.SevenDay?.UtilizationPercent ?? 0;
        var sessionReset = usage.FiveHour?.TimeUntilReset ?? "N/A";
        var weeklyReset = usage.SevenDay?.TimeUntilReset ?? "N/A";

        _trayIcon.UpdateToolTip($"Claude Session\n{LocalizationService.T("tooltip_session", sessionPct, sessionReset)}");
        _weeklyTrayIcon.UpdateToolTip($"Claude Weekly\n{LocalizationService.T("tooltip_weekly", weeklyPct, weeklyReset)}");

        if (_modelTrayIcon != null)
        {
            var modelLimit = usage.ModelWeekly;
            if (modelLimit == null)
            {
                _modelTrayIcon.UpdateToolTip($"Claude Model - {LocalizationService.T("no_data")}");
            }
            else
            {
                var modelWindow = modelLimit.Window;
                _modelTrayIcon.UpdateToolTip($"Claude {modelLimit.ModelName}\n{LocalizationService.T("tooltip_weekly", modelWindow.UtilizationPercent, modelWindow.TimeUntilReset)}");
            }
        }

        if (_overageTrayIcon != null && usage.ExtraUsage != null)
        {
            var overage = usage.ExtraUsage;
            _overageTrayIcon.UpdateToolTip($"Claude Overage\n{overage.UtilizationPercent}% | {overage.UsedFormatted} / {overage.LimitFormatted}");
        }
    }

    private void HandleFetchError(string localizationKey)
    {
        UpdateTrayIconError();
        var msg = LocalizationService.T(localizationKey);
        _trayIcon!.UpdateToolTip($"Claude Session - {msg}");
        _weeklyTrayIcon!.UpdateToolTip($"Claude Weekly - {msg}");
        _modelTrayIcon?.UpdateToolTip($"Claude Model - {msg}");
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

        _lastUsageData = usage;
        _lastSuccessfulRefresh = DateTimeOffset.UtcNow;

        UpdateTrayIcon();

        return true;
    }
}
