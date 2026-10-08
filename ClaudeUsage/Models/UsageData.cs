using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeUsage.Services;

namespace ClaudeUsage.Models;

[JsonSerializable(typeof(UsageData))]
[JsonSerializable(typeof(CredentialsFile))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class AppJsonContext : JsonSerializerContext;

public class UsageData
{
    [JsonPropertyName("five_hour")]
    public UsageWindow? FiveHour { get; set; }

    [JsonPropertyName("seven_day")]
    public UsageWindow? SevenDay { get; set; }

    [JsonPropertyName("extra_usage")]
    public ExtraUsageData? ExtraUsage { get; set; }

    [JsonPropertyName("limits")]
    public List<UsageLimit>? Limits { get; set; }

    /// <summary>
    /// The weekly quota scoped to a single model (e.g. Fable), if the plan has one.
    /// Classified on kind, never on the display label, since the scoped model changes over time.
    /// </summary>
    public UsageLimit? ModelWeekly =>
        Limits?.FirstOrDefault(l => l.Kind == "weekly_scoped" && l.Scope?.Model?.DisplayName != null);
}

public class UsageLimit
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("group")]
    public string? Group { get; set; }

    [JsonPropertyName("percent")]
    public double Percent { get; set; }

    [JsonPropertyName("severity")]
    public string? Severity { get; set; }

    [JsonPropertyName("resets_at")]
    public DateTimeOffset? ResetsAt { get; set; }

    [JsonPropertyName("scope")]
    public LimitScope? Scope { get; set; }

    public string? ModelName => Scope?.Model?.DisplayName;

    public UsageWindow Window => new() { Utilization = Percent, ResetsAt = ResetsAt };
}

public class LimitScope
{
    [JsonPropertyName("model")]
    public LimitModel? Model { get; set; }
}

public class LimitModel
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }
}

public class UsageWindow
{
    [JsonPropertyName("utilization")]
    public double Utilization { get; set; }

    [JsonPropertyName("resets_at")]
    public DateTimeOffset? ResetsAt { get; set; }

    public int UtilizationPercent => (int)Utilization;

    public double? GetElapsedPercent(int periodSeconds)
    {
        if (ResetsAt is not { } resetsAt)
            return null;

        var remaining = (resetsAt - DateTimeOffset.UtcNow).TotalSeconds;
        var elapsed = periodSeconds - remaining;
        return Math.Clamp(elapsed / periodSeconds * 100, 0, 100);
    }

    public string TimeUntilReset
    {
        get
        {
            if (ResetsAt is not { } resetsAt)
                return "—";

            var remaining = resetsAt - DateTimeOffset.UtcNow;
            if (remaining.TotalSeconds <= 0)
                return "now";

            if (remaining.TotalDays >= 1)
                return $"{(int)remaining.TotalDays}d {remaining.Hours}h";

            if (remaining.TotalHours >= 1)
                return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";

            return $"{remaining.Minutes}m";
        }
    }
}

public class ExtraUsageData
{
    [JsonPropertyName("is_enabled")]
    public bool IsEnabled { get; set; }

    [JsonPropertyName("monthly_limit")]
    public double? MonthlyLimit { get; set; }

    [JsonPropertyName("used_credits")]
    public double? UsedCredits { get; set; }

    [JsonPropertyName("utilization")]
    public double? Utilization { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    /// <summary>
    /// Credits are in minor units (e.g. cents); this is the power of ten to divide by.
    /// </summary>
    [JsonPropertyName("decimal_places")]
    public int? DecimalPlaces { get; set; }

    public int UtilizationPercent => MonthlyLimit is > 0 ? (int)((UsedCredits ?? 0) / MonthlyLimit.Value * 100) : 0;

    public string UsedFormatted => FormatAmount(UsedCredits);
    public string LimitFormatted => FormatAmount(MonthlyLimit);

    private string FormatAmount(double? minorUnits)
    {
        var places = DecimalPlaces ?? 2;
        var amount = (minorUnits ?? 0) / Math.Pow(10, places);
        var text = amount.ToString($"F{places}");
        return Currency is null or "USD" ? $"${text}" : $"{text} {Currency}";
    }
}

public class CredentialsFile
{
    [JsonPropertyName("claudeAiOauth")]
    public ClaudeOAuth? ClaudeAiOauth { get; set; }
}

public class ClaudeOAuth
{
    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expiresAt")]
    public long? ExpiresAt { get; set; }

    [JsonPropertyName("scopes")]
    public string[]? Scopes { get; set; }

    [JsonPropertyName("subscriptionType")]
    public string? SubscriptionType { get; set; }
}
