using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeUsage.Helpers;
using ClaudeUsage.Models;

namespace ClaudeUsage;

/// <summary>
/// Borderless always-on-top overlay showing session and weekly usage percentages.
/// Left-click: toggles visibility (or click vs. drag to move). Right-click: same native tray menu as the session icon.
/// </summary>
public partial class HudWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private static readonly IntPtr HwndTopMost = new(-1);
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;

    private readonly HudSettings _settings;
    private DateTimeOffset _lastTopmostUtc = DateTimeOffset.MinValue;
    private readonly DispatcherTimer _presentationTimer;
    private Point _pressPoint;
    private bool _dragging;
    private bool _allowClose;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    /// <summary>Invoked with screen coordinates when the user right-clicks; shows the same native menu as the tray.</summary>
    private readonly Action<double, double>? _showTrayContextMenuAtScreen;

    public HudWindow(HudSettings settings, Action<double, double>? showTrayContextMenuAtScreen = null)
    {
        _settings = settings;
        _showTrayContextMenuAtScreen = showTrayContextMenuAtScreen;
        InitializeComponent();
        // Tray / taskbar menus steal topmost z-order; reassert so the HUD stays above the shell.
        _presentationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _presentationTimer.Tick += (_, _) => MaintainPresentation();
        Loaded += OnHudLoaded;
        Closed += (_, _) => _presentationTimer.Stop();
    }

    private void OnHudLoaded(object sender, RoutedEventArgs e)
    {
        _presentationTimer.Start();
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (hwnd != IntPtr.Zero)
            ReassertTopmost(hwnd);
    }

    /// <summary>Allows the window to close cleanly on app shutdown (otherwise <see cref="OnClosing"/> is canceled).</summary>
    public void AllowClose()
    {
        _allowClose = true;
        Close();
    }

    /// <summary>Loads saved position or places the HUD above the taskbar at the bottom-right.</summary>
    public void ApplyStartupPosition()
    {
        if (_settings.Left.HasValue && _settings.Top.HasValue)
        {
            Left = _settings.Left.Value;
            Top = _settings.Top.Value;
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            const double margin = 8;
            Left = wa.Right - Width - margin;
            Top = wa.Bottom - Height - margin;
        }
    }

    /// <summary>Updates session/weekly labels and block colors from the same data as the tray icons.</summary>
    public void SetUsageDisplay(string sessionText, string weeklyText, Color sessionBg, Color weeklyBg)
    {
        SessionText.Text = sessionText;
        WeeklyText.Text = weeklyText;
        SessionBlock.Background = new SolidColorBrush(sessionBg);
        WeeklyBlock.Background = new SolidColorBrush(weeklyBg);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle);
        var newStyle = new IntPtr(exStyle.ToInt64() | WsExNoActivate | WsExToolWindow);
        SetWindowLongPtr(hwnd, GwlExStyle, newStyle);
        ReassertTopmost(hwnd);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            // Cancel only. Do not hide here: the shell sometimes posts WM_CLOSE to WS_EX_TOOLWINDOW
            // windows when the taskbar or desktop is activated; hiding would wrongly clear the HUD.
            e.Cancel = true;
        }
        base.OnClosing(e);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = e.GetPosition(this);
        _dragging = false;
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _pressPoint.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(pos.Y - _pressPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            _dragging = true;
            try { DragMove(); }
            catch { /* ignored */ }
        }
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging && e.ChangedButton == MouseButton.Left)
            ToggleHudVisibility();
        _dragging = false;
    }

    private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_showTrayContextMenuAtScreen == null) return;
        var pt = PointToScreen(e.GetPosition(this));
        _showTrayContextMenuAtScreen(pt.X, pt.Y);
    }

    /// <summary>Re-pin above the shell before showing a native popup (taskbar also uses topmost).</summary>
    public void ReassertTopmostForShell()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        ReassertTopmost(hwnd);
        _lastTopmostUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>Visibility toggle (hide/show) when the user clicks without dragging.</summary>
    private void ToggleHudVisibility() => ToggleFromTray();

    /// <summary>Shows or hides the overlay from the tray menu (same as a simple click toggle).</summary>
    public void ToggleFromTray()
    {
        if (Visibility == Visibility.Visible)
        {
            Visibility = Visibility.Hidden;
            _settings.Visible = false;
        }
        else
        {
            Visibility = Visibility.Visible;
            Show();
            _settings.Visible = true;
        }
        HudSettingsStore.Save(_settings);
    }

    /// <summary>
    /// If the user still wants the overlay (saved preference), ensure it is shown and topmost.
    /// Recovers from shell quirks that hide the window without going through the tray.
    /// </summary>
    public void EnsureVisibleIfRequested() => MaintainPresentation();

    /// <summary>
    /// Keeps the HUD visible and pinned above the taskbar when enabled; Win32 topmost survives tray menu z-order changes.
    /// </summary>
    private void MaintainPresentation()
    {
        if (!_settings.Visible) return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        if (Visibility != Visibility.Visible)
        {
            Visibility = Visibility.Visible;
            Show();
            ReassertTopmost(hwnd);
            _lastTopmostUtc = DateTimeOffset.UtcNow;
            return;
        }

        // Avoid SetWindowPos every tick + on every usage refresh — that caused a visible flash after tray menu actions.
        var now = DateTimeOffset.UtcNow;
        if (now - _lastTopmostUtc < TimeSpan.FromMilliseconds(750)) return;
        _lastTopmostUtc = now;
        ReassertTopmost(hwnd);
    }

    private void ReassertTopmost(IntPtr hwnd) =>
        SetWindowPos(hwnd, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

    /// <summary>Persists last position when the HUD is moved.</summary>
    private void Window_LocationChanged(object sender, EventArgs e)
    {
        if (!IsLoaded || Visibility != Visibility.Visible) return;
        _settings.Left = Left;
        _settings.Top = Top;
        HudSettingsStore.Save(_settings);
    }
}
