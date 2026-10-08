using System.IO;
using System.Text.Json;
using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

public class CredentialService
{
    private static string? _cachedCredentialsPath;
    private static DateTime _cacheTimestamp = DateTime.MinValue;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);

    private static string GetWindowsNativePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            ".credentials.json"
        );
    }

    private static async Task<string?> FindCredentialsPathAsync()
    {
        // Fast path: return cached path if still valid
        if (_cachedCredentialsPath != null
            && File.Exists(_cachedCredentialsPath)
            && DateTime.UtcNow - _cacheTimestamp < CacheLifetime)
        {
            return _cachedCredentialsPath;
        }

        var candidates = new List<(string Path, DateTime LastModified)>();

        // 1. Check Windows native path first (instant local FS check)
        var windowsPath = GetWindowsNativePath();
        if (File.Exists(windowsPath))
        {
            try
            {
                candidates.Add((windowsPath, File.GetLastWriteTimeUtc(windowsPath)));
                System.Diagnostics.Debug.WriteLine($"Found native Windows credentials at: {windowsPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error reading native credentials timestamp: {ex.Message}");
                candidates.Add((windowsPath, DateTime.MinValue));
            }
        }

        // 2. Check WSL paths (try both \\wsl$ and \\wsl.localhost)
        await Task.Run(() =>
        {
            string[] wslDistros = ["Debian", "Ubuntu", "Ubuntu-22.04", "Ubuntu-20.04", "kali-linux"];
            string[] wslRoots = [@"\\wsl.localhost", @"\\wsl$"];

            foreach (var wslRoot in wslRoots)
            foreach (var distro in wslDistros)
            {
                try
                {
                    var wslHomePath = $@"{wslRoot}\{distro}\home";
                    if (!Directory.Exists(wslHomePath)) continue;

                    foreach (var userDir in Directory.GetDirectories(wslHomePath))
                    {
                        var credPath = Path.Combine(userDir, ".claude", ".credentials.json");
                        if (File.Exists(credPath))
                        {
                            try
                            {
                                candidates.Add((credPath, File.GetLastWriteTimeUtc(credPath)));
                            }
                            catch
                            {
                                candidates.Add((credPath, DateTime.MinValue));
                            }
                            System.Diagnostics.Debug.WriteLine($"Found WSL credentials at: {credPath}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"WSL path error ({wslRoot}\\{distro}): {ex.Message}");
                }
            }
        }).WaitAsync(TimeSpan.FromSeconds(10));

        if (candidates.Count == 0)
        {
            _cachedCredentialsPath = null;
            return null;
        }

        // Pick the most recently modified credentials file
        var best = candidates.OrderByDescending(c => c.LastModified).First();
        _cachedCredentialsPath = best.Path;
        _cacheTimestamp = DateTime.UtcNow;

        System.Diagnostics.Debug.WriteLine($"Selected credentials: {best.Path} (modified {best.LastModified:u})");
        if (candidates.Count > 1)
        {
            System.Diagnostics.Debug.WriteLine($"  ({candidates.Count} candidates found, picked most recent)");
        }

        return best.Path;
    }

    /// <summary>
    /// Reads the access token from Claude Code's credentials file. Refreshing is left to
    /// Claude Code: refresh tokens are single-use, so refreshing here would sign it out.
    /// </summary>
    public static async Task<string?> GetAccessTokenAsync()
    {
        try
        {
            var credentialsPath = await FindCredentialsPathAsync();
            if (credentialsPath == null)
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(credentialsPath);
            var credentials = JsonSerializer.Deserialize(json, AppJsonContext.Default.CredentialsFile);

            var oauth = credentials?.ClaudeAiOauth;
            if (oauth == null)
            {
                return null;
            }

            if (oauth.ExpiresAt is { } expiresAt && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= expiresAt)
            {
                System.Diagnostics.Debug.WriteLine("Access token expired; waiting for Claude Code to refresh it");
                return null;
            }

            return oauth.AccessToken;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting access token: {ex.Message}");
            return null;
        }
    }
}
