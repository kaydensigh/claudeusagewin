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
    private (int pct, Drawing.Color color) _lastSessionState;
    private (int pct, Drawing.Color color) _lastWeeklyState;
    private (int pct, Drawing.Color color) _lastSonnetState;
    private (int pct, Drawing.Color color) _lastOverageState;

    // Shared colors
    private static readonly Drawing.Color ColorGray = Drawing.Color.FromArgb(156, 163, 175);
    private static readonly Drawing.Color ColorRed = Drawing.Color.FromArgb(239, 68, 68);
    private static readonly Drawing.Color ColorGreen = Drawing.Color.FromArgb(34, 197, 94);
    private static readonly Drawing.Color ColorYellow = Drawing.Color.FromArgb(234, 179, 8);

    // Window period durations
    private const int FiveHourSeconds = 5 * 3600;
    private const int SevenDaySeconds = 7 * 24 * 3600;

    // Adaptive polling
    private const int PollNormal = 420;       // 7 min
    private const int PollFast = 300;         // 5 min
    private const int PollIdle = 1200;        // 20 min
    private const int PollError = 60;         // 1 min after errors
    private const int PollFastExtra = 2;      // Extra fast polls after usage increase
    private const int MaxBackoff = 1200;      // 20 min max backoff
    private const int IdleThreshold = 600;    // 10 min idle before slow polling

    // Shared font for icon rendering (reused across CreateUsageIcon calls)
    private static readonly Drawing.Font IconFont = new("Segoe UI Semibold", 10, Drawing.FontStyle.Regular);

    private int _fastPollsRemaining;
    private int _consecutiveErrors;
    private double _previousFiveHourPct = -1;

    public async void Start()
    {
        _syncContext = SynchronizationContext.Current;

        // Initialize localization (saved preference or auto-detect)
        var savedLang = StartupHelper.GetSavedLanguage();
        LocalizationService.Initialize(savedLang);

        // Create the tray icon
        CreateTrayIcon();

        // Set up adaptive refresh timer
        _refreshTimer = new Timer(_ =>
        {
            // Marshal back to the STA thread for UI work.
            // Wrap in try/catch because Post callback is async void.
            _syncContext?.Post(async _ =>
            {
                try { await AdaptivePoll(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Poll error: {ex.Message}"); }
            }, null);
        }, null, PollNormal * 1000, Timeout.Infinite);

        // Initial data fetch
        try { await RefreshUsageData(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Initial fetch error: {ex.Message}"); }
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

    private async Task AdaptivePoll()
    {
        // Check if user is idle/locked — use slower polling
        var isIdle = IdleHelper.IsUserAway(IdleThreshold);

        await RefreshUsageData();

        // Calculate next interval based on result
        var nextInterval = CalculatePollInterval(isIdle);
        _refreshTimer!.Change(nextInterval * 1000, Timeout.Infinite);

        System.Diagnostics.Debug.WriteLine(
            $"Adaptive poll: next in {nextInterval}s (fast={_fastPollsRemaining}, errors={_consecutiveErrors}, idle={isIdle})");
    }

    private int CalculatePollInterval(bool isIdle)
    {
        // Error backoff
        if (_consecutiveErrors > 0)
        {
            var backoff = (int)(PollError * Math.Pow(2, Math.Min(_consecutiveErrors - 1, 4)));
            return Math.Min(backoff, MaxBackoff);
        }

        if (isIdle)
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
        using var textBrush = new Drawing.SolidBrush(Drawing.Color.White);

        var text = percentage.ToString();
        var textSize = g.MeasureString(text, IconFont);
        var textX = (size - textSize.Width) / 2 + 1;
        var textY = (size - textSize.Height) / 2 + 1;
        g.DrawString(text, IconFont, textBrush, textX, textY);

        // Icon.FromHandle does NOT own the HICON — we must clone and destroy.
        var hIcon = bitmap.GetHicon();
        var icon = (Drawing.Icon)Drawing.Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

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
    /// Swap a tray icon only if the percentage or color has changed.
    /// </summary>
    private void SwapIcon(ref Drawing.Icon? iconField, ref (int pct, Drawing.Color color) lastState,
        TrayIconWithContextMenu tray, int pct, Drawing.Color color)
    {
        if (lastState.pct == pct && lastState.color == color && iconField != null)
            return;

        var old = iconField;
        iconField = CreateUsageIcon(pct, color);
        tray.UpdateIcon(iconField.Handle);
        old?.Dispose();
        lastState = (pct, color);
    }

    private void UpdateTrayIconError()
    {
        SwapIcon(ref _currentIcon, ref _lastSessionState, _trayIcon!, 0, ColorGray);
        SwapIcon(ref _weeklyIcon, ref _lastWeeklyState, _weeklyTrayIcon!, 0, ColorGray);
        if (_sonnetTrayIcon != null)
            SwapIcon(ref _sonnetIcon, ref _lastSonnetState, _sonnetTrayIcon, 0, ColorGray);
        if (_overageTrayIcon != null)
            SwapIcon(ref _overageIcon, ref _lastOverageState, _overageTrayIcon, 0, ColorGray);
    }

    private void UpdateTrayIcon()
    {
        if (_lastUsageData == null) return;

        var showDetails = StartupHelper.GetShowDetails();

        // Update session icon
        var sessionWindow = _lastUsageData.FiveHour;
        var sessionUtilPct = sessionWindow?.Utilization ?? 0;
        var sessionElapsedPct = sessionWindow?.GetElapsedPercent(FiveHourSeconds) ?? 0;
        SwapIcon(ref _currentIcon, ref _lastSessionState, _trayIcon!,
            (int)sessionUtilPct, GetColorForUsageElapsed(sessionUtilPct, sessionElapsedPct));

        // Update weekly icon
        var weeklyWindow = _lastUsageData.SevenDay;
        var weeklyUtilPct = weeklyWindow?.Utilization ?? 0;
        var weeklyElapsedPct = weeklyWindow?.GetElapsedPercent(SevenDaySeconds) ?? 0;
        SwapIcon(ref _weeklyIcon, ref _lastWeeklyState, _weeklyTrayIcon!,
            (int)weeklyUtilPct, GetColorForUsageElapsed(weeklyUtilPct, weeklyElapsedPct));

        // Update sonnet icon (if visible)
        if (_sonnetTrayIcon != null && showDetails)
        {
            var sonnetWindow = _lastUsageData.Sonnet;
            var sonnetUtilPct = sonnetWindow?.Utilization ?? 0;
            var sonnetElapsedPct = sonnetWindow?.GetElapsedPercent(SevenDaySeconds) ?? 0;
            SwapIcon(ref _sonnetIcon, ref _lastSonnetState, _sonnetTrayIcon,
                (int)sonnetUtilPct, GetColorForUsageElapsed(sonnetUtilPct, sonnetElapsedPct));
        }

        // Update overage icon (if visible)
        if (_overageTrayIcon != null && showDetails && _lastUsageData.ExtraUsage != null)
        {
            var overageUtilPct = _lastUsageData.ExtraUsage.Utilization ?? 0;
            SwapIcon(ref _overageIcon, ref _lastOverageState, _overageTrayIcon,
                (int)overageUtilPct, GetColorForUsageElapsed(overageUtilPct, 50));
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
        _weeklyIcon = CreateUsageIcon(0, ColorGray);
        _sonnetIcon = CreateUsageIcon(0, ColorGray);
        _overageIcon = CreateUsageIcon(0, ColorGray);

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
        _sonnetTrayIcon = new TrayIconWithContextMenu("ClaudeUsage.Sonnet")
        {
            Icon = _sonnetIcon!.Handle,
            ToolTip = "Claude Sonnet - Loading..."
        };

        _sonnetTrayIcon.Create();
    }

    private void CreateOverageTrayIcon()
    {
        _overageTrayIcon = new TrayIconWithContextMenu("ClaudeUsage.Overage")
        {
            Icon = _overageIcon!.Handle,
            ToolTip = "Claude Overage - Loading..."
        };
        _overageTrayIcon.Create();
    }

    private PopupMenuItem CreateRefreshMenuItem() =>
        new(LocalizationService.T("refresh_now"), async (s, e) =>
        {
            try { await RefreshUsageData(); }
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
        _consecutiveErrors++;
        UpdateTrayIconError();
        var msg = LocalizationService.T(localizationKey);
        _trayIcon!.UpdateToolTip($"Claude Session - {msg}");
        _weeklyTrayIcon!.UpdateToolTip($"Claude Weekly - {msg}");
    }

    public async Task RefreshUsageData()
    {
        if (!CredentialService.CredentialsExist())
        {
            HandleFetchError("no_credentials");
            return;
        }

        var usage = await UsageApiService.GetUsageAsync();

        if (usage == null)
        {
            HandleFetchError("failed_to_fetch");
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

        UpdateTrayIcon();

        // Update tooltips
        var sessionPct = usage.FiveHour?.UtilizationPercent ?? 0;
        var weeklyPct = usage.SevenDay?.UtilizationPercent ?? 0;
        var sessionReset = usage.FiveHour?.TimeUntilReset ?? "N/A";
        var weeklyReset = usage.SevenDay?.TimeUntilReset ?? "N/A";

        _trayIcon!.UpdateToolTip($"Claude Session\n{LocalizationService.T("tooltip_session", sessionPct, sessionReset)}");
        _weeklyTrayIcon!.UpdateToolTip($"Claude Weekly\n{LocalizationService.T("tooltip_weekly", weeklyPct, weeklyReset)}");

        // Update sonnet and overage tooltips if visible
        var showDetails = StartupHelper.GetShowDetails();
        if (_sonnetTrayIcon != null && showDetails)
        {
            var sonnetPct = usage.Sonnet?.UtilizationPercent ?? 0;
            var sonnetReset = usage.Sonnet?.TimeUntilReset ?? "N/A";
            _sonnetTrayIcon.UpdateToolTip($"Claude Sonnet\n{sonnetPct}% used\nReset: {sonnetReset}");
        }

        if (_overageTrayIcon != null && showDetails && usage.ExtraUsage != null)
        {
            var overagePct = usage.ExtraUsage.UtilizationPercent;
            var overageUsed = usage.ExtraUsage.UsedDollars;
            var overageLimit = usage.ExtraUsage.LimitDollars;
            _overageTrayIcon.UpdateToolTip($"Claude Overage\n{overagePct}% used\n${overageUsed:F2} / ${overageLimit:F2}");
        }
    }
}
