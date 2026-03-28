using System.IO;
using System.Text.Json;
using ClaudeUsage.Models;

namespace ClaudeUsage.Helpers;

/// <summary>
/// Loads and saves HUD settings to %LocalAppData%\ClaudeUsage\hud.json.
/// </summary>
public static class HudSettingsStore
{
    private static string GetPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeUsage");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "hud.json");
    }

    /// <summary>Loads settings, or returns defaults if missing or invalid.</summary>
    public static HudSettings Load()
    {
        try
        {
            var path = GetPath();
            if (!File.Exists(path)) return new HudSettings();

            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize(json, AppJsonContext.Default.HudSettings);
            return loaded ?? new HudSettings();
        }
        catch
        {
            return new HudSettings();
        }
    }

    /// <summary>Persists settings; failures are ignored.</summary>
    public static void Save(HudSettings settings)
    {
        try
        {
            var path = GetPath();
            var json = JsonSerializer.Serialize(settings, AppJsonContext.Default.HudSettings);
            File.WriteAllText(path, json);
        }
        catch
        {
            // ignore
        }
    }
}
