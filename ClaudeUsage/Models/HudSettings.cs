using System.Text.Json.Serialization;

namespace ClaudeUsage.Models;

/// <summary>
/// Persisted HUD overlay position and visibility (JSON in LocalAppData).
/// </summary>
public sealed class HudSettings
{
    [JsonPropertyName("left")]
    public double? Left { get; set; }

    [JsonPropertyName("top")]
    public double? Top { get; set; }

    /// <summary>Whether the HUD should be shown when the app starts.</summary>
    [JsonPropertyName("visible")]
    public bool Visible { get; set; } = true;
}
