using ClaudeUsage.Helpers;
using ClaudeUsage.Models;
using ClaudeUsage.Services;
using Svg;
using H.NotifyIcon.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ClaudeUsage;

public class App
{
    private TrayIconWithContextMenu? _trayIcon;
    private TrayIconWithContextMenu? _weeklyTrayIcon;
    private TrayIconWithContextMenu? _sonnetTrayIcon;
    private TrayIconWithContextMenu? _overageTrayIcon;
    private Forms.Timer? _refreshTimer;
    private UsageData? _lastUsageData;
    private PopupMenu? _contextMenu;
    private PopupMenu? _weeklyContextMenu;
    private PopupMenuItem? _launchAtLoginItem;
    private PopupMenuItem? _showDetailsItem;

    private Drawing.Icon? _currentIcon;
    private Drawing.Icon? _weeklyIcon;
    private Drawing.Icon? _sonnetIcon;
    private Drawing.Icon? _overageIcon;

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

    public async void Start()
    {
        // Initialize localization (saved preference or auto-detect)
        var savedLang = StartupHelper.GetSavedLanguage();
        LocalizationService.Initialize(savedLang);

        // Create the tray icon
        CreateTrayIcon();

        // Set up adaptive refresh timer
        _refreshTimer = new Forms.Timer
        {
            Interval = PollNormal * 1000
        };
        _refreshTimer.Tick += async (s, args) => await AdaptivePoll();
        _refreshTimer.Start();

        // Initial data fetch
        await RefreshUsageData();
    }

    public void Shutdown()
    {
        _refreshTimer?.Stop();
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

        if (isIdle)
        {
            _refreshTimer!.Interval = PollIdle * 1000;
            // Still poll, just slower
        }

        await RefreshUsageData();

        // Calculate next interval based on result
        var nextInterval = CalculatePollInterval();
        _refreshTimer!.Interval = nextInterval * 1000;

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
        var frameColor = Drawing.Color.White;

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
        var adjustedUtilization = double.Max(0, utilizationPercent - 10) / 90.0 * 100;
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
        var oldSonnetIcon = _sonnetIcon;
        var oldOverageIcon = _overageIcon;
        var svgDoc = LoadSvgFromResource("error.svg");
        if (svgDoc != null)
        {
            _currentIcon = CreateIconFromSvg(svgDoc, Drawing.Color.FromArgb(156, 163, 175));
            _trayIcon!.UpdateIcon(_currentIcon.Handle);
            _weeklyIcon = CreateIconFromSvg(svgDoc, Drawing.Color.FromArgb(156, 163, 175));
            _weeklyTrayIcon!.UpdateIcon(_weeklyIcon.Handle);
            if (_sonnetTrayIcon != null)
            {
                _sonnetIcon = CreateIconFromSvg(svgDoc, Drawing.Color.FromArgb(156, 163, 175));
                _sonnetTrayIcon.UpdateIcon(_sonnetIcon.Handle);
            }
            if (_overageTrayIcon != null)
            {
                _overageIcon = CreateIconFromSvg(svgDoc, Drawing.Color.FromArgb(156, 163, 175));
                _overageTrayIcon.UpdateIcon(_overageIcon.Handle);
            }
        }
        oldIcon?.Dispose();
        oldWeeklyIcon?.Dispose();
        oldSonnetIcon?.Dispose();
        oldOverageIcon?.Dispose();
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

        // Update sonnet icon (if visible)
        if (_sonnetTrayIcon != null && StartupHelper.GetShowDetails())
        {
            var sonnetWindow = _lastUsageData.Sonnet;
            var sonnetUtilPct = sonnetWindow?.Utilization ?? 0;
            var sonnetElapsedPct = sonnetWindow?.GetElapsedPercent(7 * 24 * 3600) ?? 0;
            var sonnetColor = GetColorForUsageElapsed(sonnetUtilPct, sonnetElapsedPct);

            var oldSonnetIcon = _sonnetIcon;
            _sonnetIcon = CreateUsageIcon((int)sonnetUtilPct, sonnetColor);
            _sonnetTrayIcon.UpdateIcon(_sonnetIcon.Handle);
            oldSonnetIcon?.Dispose();
        }

        // Update overage icon (if visible)
        if (_overageTrayIcon != null && StartupHelper.GetShowDetails() && _lastUsageData.ExtraUsage != null)
        {
            var extra = _lastUsageData.ExtraUsage;
            var overageUtilPct = extra.Utilization ?? 0;
            var overageColor = GetColorForUsageElapsed(overageUtilPct, 50); // Assume 50% elapsed as baseline

            var oldOverageIcon = _overageIcon;
            _overageIcon = CreateUsageIcon((int)overageUtilPct, overageColor);
            _overageTrayIcon.UpdateIcon(_overageIcon.Handle);
            oldOverageIcon?.Dispose();
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
        _currentIcon = CreateUsageIcon(0, Drawing.Color.FromArgb(156, 163, 175)); // Gray
        _weeklyIcon = CreateUsageIcon(0, Drawing.Color.FromArgb(156, 163, 175)); // Gray
        _sonnetIcon = CreateUsageIcon(0, Drawing.Color.FromArgb(156, 163, 175));
        _overageIcon = CreateUsageIcon(0, Drawing.Color.FromArgb(156, 163, 175));

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

    private void CreateWeeklyContextMenu()
    {
        var refreshItem = new PopupMenuItem(LocalizationService.T("refresh_now"), async (s, e) =>
        {
            await Task.Run(() => _ = RefreshUsageData());
        });

        var exitItem = new PopupMenuItem(LocalizationService.T("exit"), (s, e) =>
        {
            RemoveAllTrayIcons();
            Forms.Application.Exit();
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

        _weeklyTrayIcon!.ContextMenu = _weeklyContextMenu;
    }

    private void CreateContextMenu()
    {
        var refreshItem = new PopupMenuItem(LocalizationService.T("refresh_now"), async (s, e) =>
        {
            await Task.Run(() => _ = RefreshUsageData());
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

        var exitItem = new PopupMenuItem(LocalizationService.T("exit"), (s, e) =>
        {
            RemoveAllTrayIcons();
            Forms.Application.Exit();
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

        UpdateTrayIcon();

        // Update tooltips
        var sessionPct = usage.FiveHour?.UtilizationPercent ?? 0;
        var weeklyPct = usage.SevenDay?.UtilizationPercent ?? 0;
        var sessionReset = usage.FiveHour?.TimeUntilReset ?? "N/A";
        var weeklyReset = usage.SevenDay?.TimeUntilReset ?? "N/A";

        _trayIcon!.UpdateToolTip($"Claude Session\n{LocalizationService.T("tooltip_session", sessionPct, sessionReset)}");
        _weeklyTrayIcon!.UpdateToolTip($"Claude Weekly\n{LocalizationService.T("tooltip_weekly", weeklyPct, weeklyReset)}");

        // Update sonnet and overage tooltips if visible
        if (_sonnetTrayIcon != null && StartupHelper.GetShowDetails())
        {
            var sonnetPct = usage.Sonnet?.UtilizationPercent ?? 0;
            var sonnetReset = usage.Sonnet?.TimeUntilReset ?? "N/A";
            _sonnetTrayIcon.UpdateToolTip($"Claude Sonnet\n{sonnetPct}% used\nReset: {sonnetReset}");
        }

        if (_overageTrayIcon != null && StartupHelper.GetShowDetails() && usage.ExtraUsage != null)
        {
            var overagePct = usage.ExtraUsage.UtilizationPercent;
            var overageUsed = usage.ExtraUsage.UsedDollars;
            var overageLimit = usage.ExtraUsage.LimitDollars;
            _overageTrayIcon.UpdateToolTip($"Claude Overage\n{overagePct}% used\n${overageUsed:F2} / ${overageLimit:F2}");
        }
    }
}
