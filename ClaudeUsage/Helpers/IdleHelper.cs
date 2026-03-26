using System.Runtime.InteropServices;

namespace ClaudeUsage.Helpers;

public static class IdleHelper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    /// <summary>
    /// Returns seconds since last keyboard/mouse input.
    /// </summary>
    public static int GetIdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
            return 0;

        // Use bitmask for unsigned DWORD overflow after ~24.8 days of uptime
        var idleMs = (GetTickCount() - info.dwTime) & 0xFFFFFFFF;
        return (int)(idleMs / 1000);
    }

    /// <summary>
    /// Returns true if the workstation is locked (secure desktop active).
    /// </summary>
    public static bool IsWorkstationLocked()
    {
        var hDesktop = OpenInputDesktop(0, false, 0);
        if (hDesktop == IntPtr.Zero)
            return true; // Can't open desktop = locked

        CloseDesktop(hDesktop);
        return false;
    }

    /// <summary>
    /// Returns true if the user is away (locked or idle beyond threshold).
    /// </summary>
    public static bool IsUserAway(int idleThresholdSeconds)
    {
        if (IsWorkstationLocked())
            return true;

        return idleThresholdSeconds > 0 && GetIdleSeconds() >= idleThresholdSeconds;
    }
}
