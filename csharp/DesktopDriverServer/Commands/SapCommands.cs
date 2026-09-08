using System.Text.Json;
using DesktopDriverServer.Sap;
using DesktopDriverServer.State;

namespace DesktopDriverServer.Commands;

/// <summary>
/// <c>sap.*</c> RPC handlers — the driver-facing surface of the SAP GUI Scripting
/// subsystem (<see cref="SapGuiClient"/>). Parallel in spirit to
/// <see cref="DotNetBridgeCommands"/> / <see cref="JabCommands"/>: an alternate tree
/// provider for a runtime standard UIA can't see into.
///
/// The lifecycle is simpler than the injected bridges — nothing to inject, the SAP
/// scripting engine is already in-process in saplogon.exe. <c>sap.attach</c> binds to
/// it; every other command needs an attached session.
/// </summary>
public static class SapCommands
{
    private static SapGuiClient Client(SessionState state) =>
        state.Sap ?? throw new InvalidOperationException(
            "SAP GUI is not attached. Call sap.attach first.");

    /// <summary>
    /// <c>sap.attach</c> — bind to the running SAP GUI scripting engine and select a
    /// connection/session. Params: <c>connectionIndex</c> (default 0),
    /// <c>sessionIndex</c> (default 0). Returns a status object; <c>attached:false</c>
    /// with <c>reason:"no_open_connection"</c> when the engine is reachable but nobody
    /// is logged in yet.
    /// </summary>
    public static object? Attach(SessionState state, JsonElement? parameters)
    {
        int connectionIndex = GetInt(parameters, "connectionIndex", 0);
        int sessionIndex = GetInt(parameters, "sessionIndex", 0);

        var client = state.EnableSap();
        return client.Attach(connectionIndex, sessionIndex);
    }

    /// <summary><c>sap.detach</c> — drop the SAP session reference.</summary>
    public static object? Detach(SessionState state, JsonElement? parameters)
    {
        state.DisableSap();
        return new { detached = true };
    }

    /// <summary><c>sap.status</c> — whether a SAP session is currently attached.</summary>
    public static object? Status(SessionState state, JsonElement? parameters) =>
        new { attached = state.Sap?.IsAttached ?? false };

    /// <summary>
    /// <c>sap.pageSource</c> — XML dump of the SAP component tree under
    /// <c>contextElementId</c> (or the active window). Same shape as the driver's other
    /// page sources.
    /// </summary>
    public static object? PageSource(SessionState state, JsonElement? parameters) =>
        Client(state).GetPageSourceXml(GetString(parameters, "contextElementId"));

    /// <summary><c>sap.dumpTree</c> — alias of <c>sap.pageSource</c>, for the inspector tool.</summary>
    public static object? DumpTree(SessionState state, JsonElement? parameters) =>
        Client(state).DumpTree(GetString(parameters, "contextElementId"));

    /// <summary>
    /// <c>sap.findElement</c> — resolve a SAP id locator. Params: <c>id</c> (a SAP id
    /// path, with or without the <c>sap:</c> prefix). Returns the prefixed element id or
    /// null. XPath locators go through <c>sap.evaluateXPath</c> instead.
    /// </summary>
    public static object? FindElement(SessionState state, JsonElement? parameters)
    {
        var id = GetString(parameters, "id")
            ?? throw new ArgumentException("sap.findElement requires 'id'.");
        return Client(state).FindFirstById(SapGuiClient.RawId(id));
    }

    /// <summary><c>sap.evaluateXPath</c> — full XPath 1.0 over the SAP subtree.</summary>
    public static object? EvaluateXPath(SessionState state, JsonElement? parameters)
    {
        var p = parameters ?? throw new ArgumentException("Parameters required.");
        var expression = p.GetProperty("expression").GetString()
            ?? throw new ArgumentException("expression is required.");
        bool multiple = p.TryGetProperty("multiple", out var m) && m.ValueKind == JsonValueKind.True;
        return Client(state).EvaluateXPath(GetString(parameters, "contextElementId"), expression, multiple);
    }

    public static object? GetProperty(SessionState state, JsonElement? parameters)
    {
        var id = RequireId(parameters);
        var property = GetString(parameters, "property")
            ?? throw new ArgumentException("sap.getProperty requires 'property'.");
        return Client(state).GetProperty(id, property);
    }

    public static object? GetText(SessionState state, JsonElement? parameters) =>
        Client(state).GetText(RequireId(parameters));

    public static object? GetTagName(SessionState state, JsonElement? parameters) =>
        Client(state).GetTagName(RequireId(parameters));

    public static object? GetRect(SessionState state, JsonElement? parameters) =>
        Client(state).GetRect(RequireId(parameters));

    public static object? SetValue(SessionState state, JsonElement? parameters)
    {
        var id = RequireId(parameters);
        var value = GetString(parameters, "value") ?? "";
        Client(state).SetValue(id, value);
        return null;
    }

    public static object? Invoke(SessionState state, JsonElement? parameters)
    {
        Client(state).Invoke(RequireId(parameters));
        return null;
    }

    public static object? SetFocus(SessionState state, JsonElement? parameters)
    {
        Client(state).SetFocus(RequireId(parameters));
        return null;
    }

    public static object? Select(SessionState state, JsonElement? parameters)
    {
        Client(state).Select(RequireId(parameters));
        return null;
    }

    /// <summary><c>sap.sendVKey</c> — params: <c>vkey</c> (int), optional <c>windowElementId</c>.</summary>
    public static object? SendVKey(SessionState state, JsonElement? parameters)
    {
        int vkey = GetInt(parameters, "vkey", 0);
        Client(state).SendVKey(vkey, GetString(parameters, "windowElementId"));
        return null;
    }

    // ── param helpers ──────────────────────────────────────────────────────────

    private static string RequireId(JsonElement? p) =>
        GetString(p, "elementId") ?? GetString(p, "id")
        ?? throw new ArgumentException("This sap command requires 'elementId'.");

    private static string? GetString(JsonElement? p, string name) =>
        p?.TryGetProperty(name, out var v) == true && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int GetInt(JsonElement? p, string name, int fallback) =>
        p?.TryGetProperty(name, out var v) == true && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : fallback;
}
