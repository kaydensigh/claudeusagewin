using Microsoft.Win32;

namespace ClaudeUsage.Helpers;

public static class StartupHelper
{
    private const string AppName = "ClaudeUsage";
    private const string RegistryKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string SettingsKeyPath = @"SOFTWARE\ClaudeUsage";

    public static bool IsLaunchAtLoginEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, false);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetLaunchAtLogin(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(AppName, $"\"{exePath}\"");
                }
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch
        {
            // Silently fail if registry access is denied
        }
    }

    public static bool GetShowDetails()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, false);
            return key?.GetValue("ShowDetails") is int val && val != 0;
        }
        catch
        {
            return false;
        }
    }

    public static void SetShowDetails(bool show)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath);
            key.SetValue("ShowDetails", show ? 1 : 0, RegistryValueKind.DWord);
        }
        catch
        {
            // Silently fail
        }
    }

    public static string? GetSavedLanguage()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, false);
            return key?.GetValue("Language") as string;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveLanguage(string langCode)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath);
            key.SetValue("Language", langCode);
        }
        catch
        {
            // Silently fail
        }
    }
}
