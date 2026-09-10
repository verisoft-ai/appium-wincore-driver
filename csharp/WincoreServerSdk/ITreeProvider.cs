using System.Xml;

namespace Wincore.ServerSdk;

/// <summary>
/// A source of UI elements outside the real UI Automation tree — the Java (JAB /
/// AccessibleContext) agent, the .NET (WinForms / WPF / DevExpress) bridge, etc.
///
/// The host routes a command to a provider by element-id prefix (an id this
/// provider minted, e.g. <c>java:1234:56</c>) or, for a fresh find / page source /
/// xpath rooted at a window, by <see cref="OwnsWindow"/>. Every method works purely
/// in terms of opaque element-id strings that this provider itself minted and
/// caches — the host never inspects a provider element.
///
/// Implementations own their connection + element cache and must be thread-safe for
/// the host's single request-loop thread (calls are serialized).
/// </summary>
public interface ITreeProvider : IDisposable
{
    /// <summary>Stable short name for logs, e.g. "java", "dotnet".</summary>
    string Name { get; }

    /// <summary>
    /// Element-id prefixes this provider mints and answers for, e.g.
    /// <c>["java:"]</c> or <c>["dotnet:", "dotnetcore:"]</c>. Used by the host to
    /// route a command carrying an existing element id.
    /// </summary>
    IReadOnlyList<string> ElementIdPrefixes { get; }

    /// <summary>True if <paramref name="elementId"/> starts with one of <see cref="ElementIdPrefixes"/>.</summary>
    bool OwnsElementId(string elementId);

    /// <summary>True once the underlying agent/bridge is connected for this session.</summary>
    bool IsAttached { get; }

    // ── window routing ──────────────────────────────────────────────────────────

    /// <summary>
    /// True when a find / page source / xpath rooted at the given top-level window
    /// should be served from this provider's tree rather than real UIA. Only ever
    /// consulted while <see cref="IsAttached"/>.
    /// </summary>
    bool OwnsWindow(IntPtr hwnd, string windowTitle);

    /// <summary>
    /// Resolves (and caches) this provider's own root element id for a window it
    /// <see cref="OwnsWindow"/>s, or null if it cannot.
    /// </summary>
    string? GetWindowRootId(IntPtr hwnd, string windowTitle);

    /// <summary>
    /// When true, a standard <c>findElement</c> from a plain window root auto-descends
    /// into this provider's tree (Java: a Java window's children live only in the
    /// agent tree). When false the provider is opt-in only — standard find stays on
    /// real UIA and this tree is reached through the plugin's own dedicated commands
    /// (the .NET bridge's <c>*ViaDotnetBridge</c> family).
    /// </summary>
    bool AutoRouteStandardFind { get; }

    /// <summary>
    /// When true, <c>getPageSource</c> for an owned window is built from this
    /// provider's tree instead of UIA (Java: the window is an opaque pane to UIA).
    /// </summary>
    bool AutoSwapsPageSource { get; }

    // ── find ────────────────────────────────────────────────────────────────────

    string? FindFirst(string rootElementId, ConditionDto condition, string scope);
    IReadOnlyList<string> FindAll(string rootElementId, ConditionDto condition, string scope);

    /// <summary>Evaluates a full XPath against this provider's subtree. Returns a
    /// single element id (or null) when <paramref name="multiple"/> is false, else a
    /// <c>string[]</c> of ids in document order.</summary>
    object? EvaluateXPath(string rootElementId, string expression, bool multiple);

    // ── element getters ─────────────────────────────────────────────────────────

    /// <summary>
    /// Value of a UIA-named property for a provider element. The provider owns the
    /// mapping to whatever its runtime exposes (Java derives ExpandCollapseState /
    /// HasKeyboardFocus from the AccessibleState list, etc.) and any freshness
    /// re-fetch a given property needs.
    /// </summary>
    object? GetProperty(string elementId, string propertyName);

    string GetText(string elementId);
    string GetTagName(string elementId);

    /// <summary>Bounding rectangle as an object shaped <c>{ x, y, width, height }</c>.</summary>
    object GetRect(string elementId);

    string GetToggleState(string elementId);
    bool IsSelected(string elementId);
    bool IsAlive(string elementId);

    // ── interaction ─────────────────────────────────────────────────────────────

    void Invoke(string elementId);
    void SetValue(string elementId, string value);
    void Select(string elementId);
    void RequestFocus(string elementId);
    void Expand(string elementId);

    // ── page source ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Appends this provider's subtree under <paramref name="rootElementId"/> to
    /// <paramref name="doc"/>, as a child of <paramref name="parent"/> (or as the
    /// document element when parent is null). Tag names must be the same
    /// language-neutral vocabulary an XPath node test uses.
    /// </summary>
    void BuildPageSourceXml(string rootElementId, XmlDocument doc, XmlElement? parent);
}
