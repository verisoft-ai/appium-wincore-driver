using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopDriverServer.Protocol;

public class Request
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }
}

public class Response
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ErrorInfo? Error { get; set; }

    [JsonPropertyName("duration_ms")]
    public long DurationMs { get; set; }
}

public class ErrorInfo
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

// ConditionDto moved to the WincoreServerSdk project (Wincore.ServerSdk) — a server
// plugin's ITreeProvider receives it directly. Re-exported via the global using in
// GlobalUsings.cs so unqualified references across this project still resolve.
