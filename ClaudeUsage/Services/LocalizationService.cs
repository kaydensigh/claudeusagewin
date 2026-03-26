using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

public static class LocalizationService
{
    private static Dictionary<string, string> _strings = new();
    private static Dictionary<string, string> _fallback = new();
    private static string _currentLang = "en";
    private static string[]? _cachedResourceNames;

    // Language code → display name mapping
    public static readonly (string Code, string DisplayName)[] SupportedLanguages =
    [
        ("auto", "Auto"),
        ("en", "English"),
        ("de", "Deutsch"),
        ("fr", "Français"),
        ("es", "Español"),
        ("pt-BR", "Português"),
        ("it", "Italiano"),
        ("ja", "日本語"),
        ("ko", "한국어"),
        ("zh-CN", "中文(简)"),
        ("zh-TW", "中文(繁)"),
        ("hi", "हिन्दी"),
        ("id", "Indonesian"),
        ("pl", "Polski"),
        ("ru", "Русский")
    ];

    public static string CurrentLanguage => _currentLang;

    public static void Initialize(string? overrideLang = null)
    {
        // Always load English as fallback
        _fallback = LoadLanguage("en");

        var lang = overrideLang ?? DetectLanguageCode();
        _currentLang = lang;
        _strings = lang == "en" ? _fallback : LoadLanguage(lang);
    }

    public static void SetLanguage(string langCode)
    {
        if (langCode == "auto")
        {
            _currentLang = DetectLanguageCode();
        }
        else
        {
            _currentLang = langCode;
        }

        _strings = _currentLang == "en" ? _fallback : LoadLanguage(_currentLang);
    }

    /// <summary>
    /// Get a localized string by key. Falls back to English if key is missing.
    /// </summary>
    public static string T(string key)
    {
        if (_strings.TryGetValue(key, out var val)) return val;
        if (_fallback.TryGetValue(key, out var fb)) return fb;
        return key;
    }

    /// <summary>
    /// Get a localized string with format arguments.
    /// </summary>
    public static string T(string key, params object[] args)
    {
        var template = T(key);
        try
        {
            return string.Format(template, args);
        }
        catch
        {
            return template;
        }
    }

    private static string DetectLanguageCode()
    {
        var culture = CultureInfo.CurrentUICulture;

        // Try full name first (e.g., "pt-BR", "zh-CN", "zh-TW")
        var fullName = culture.Name; // e.g., "pt-BR", "en-US", "zh-Hans-CN"
        if (HasLanguage(fullName)) return fullName;

        // Try parent culture (e.g., "zh-Hans" -> check for "zh-CN")
        // Map known culture names to our file names
        if (fullName.StartsWith("zh-Hans") || fullName.StartsWith("zh-CN")) return "zh-CN";
        if (fullName.StartsWith("zh-Hant") || fullName.StartsWith("zh-TW")) return "zh-TW";
        if (fullName.StartsWith("pt")) return "pt-BR";

        // Try two-letter ISO code (e.g., "en", "de", "fr")
        var twoLetter = culture.TwoLetterISOLanguageName;
        if (HasLanguage(twoLetter)) return twoLetter;

        return "en";
    }

    private static string[] GetResourceNames()
    {
        return _cachedResourceNames ??= Assembly.GetExecutingAssembly().GetManifestResourceNames();
    }

    private static bool HasLanguage(string code)
    {
        return GetResourceNames()
            .Any(r => r.EndsWith($".Locale.{code}.json", StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, string> LoadLanguage(string code)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = GetResourceNames()
                .FirstOrDefault(r => r.EndsWith($".Locale.{code}.json", StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                System.Diagnostics.Debug.WriteLine($"Locale not found: {code}");
                return _fallback.Count > 0 ? _fallback : new Dictionary<string, string>();
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return _fallback;

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.DictionaryStringString) ?? new();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading locale {code}: {ex.Message}");
            return _fallback.Count > 0 ? _fallback : new Dictionary<string, string>();
        }
    }
}
