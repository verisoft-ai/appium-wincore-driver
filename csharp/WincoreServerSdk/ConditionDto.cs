using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wincore.ServerSdk;

/// <summary>
/// Wire representation of an Appium locator, sent by the TS client and reconstructed
/// into a real UIA3 <c>Condition</c> by the host (see <c>ConditionBuilder</c>) or
/// evaluated natively by a tree provider (Java / .NET bridges).
///
/// This type lives in the SDK because a server plugin's <see cref="ITreeProvider"/>
/// receives it directly. The JSON shape must stay byte-compatible with
/// <c>lib/server/conditions.ts</c> — do not rename properties.
/// </summary>
public class ConditionDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("property")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Property { get; set; }

    /// <summary>
    /// Optional string match mode for a property condition: "contains" or "startsWith".
    /// Absent means exact equality. Only the bridge agents (Java/.NET) evaluate this
    /// natively; for UIA it degrades to a true-condition and the caller re-verifies
    /// client-side (see ConditionBuilder.BuildPropertyCondition).
    /// </summary>
    [JsonPropertyName("match")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Match { get; set; }

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Value { get; set; }

    [JsonPropertyName("conditions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConditionDto[]? Conditions { get; set; }

    [JsonPropertyName("condition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConditionDto? Condition { get; set; }
}
