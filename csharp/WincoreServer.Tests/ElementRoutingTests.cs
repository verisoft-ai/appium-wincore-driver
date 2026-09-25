using System.Text.Json;
using System.Xml;
using Wincore.ServerSdk;
using WincoreServer.Server;
using WincoreServer.State;
using Xunit;

namespace WincoreServer.Tests;

/// <summary>
/// Element-scoped commands are routed centrally by <see cref="ElementRoute"/>: an id
/// minted by a tree provider must reach that provider (never the UIA element table),
/// UIA-only commands must reject it uniformly, and capabilities a provider doesn't
/// override must fail with the SDK's default "not supported" error.
/// </summary>
public class ElementRoutingTests
{
    private static readonly HashSet<string> ExpectedUiaOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "maximizeWindow", "minimizeWindow", "restoreWindow", "closeWindow",
        "moveWindow", "resizeWindow", "setRootElementFromElementId",
    };

    private readonly CommandDispatcher _dispatcher = new();
    private readonly SessionState _state = new();
    private readonly FakeTreeProvider _fake = new();

    public ElementRoutingTests()
    {
        _state.Providers.Register(_fake);
    }

    private object? Execute(string method, string json) =>
        _dispatcher.Execute(method, _state, JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Collapse_ProviderElement_ReachesProvider()
    {
        Execute("collapseElement", """{"elementId":"fake:1"}""");

        Assert.Equal(["Collapse:fake:1"], _fake.Calls);
    }

    [Fact]
    public void Toggle_ProviderElement_MapsToInvoke()
    {
        Execute("toggleElement", """{"elementId":"fake:1"}""");

        Assert.Equal(["Invoke:fake:1"], _fake.Calls);
    }

    [Fact]
    public void GetElementValue_ProviderElement_MapsToGetText()
    {
        var result = Execute("getElementValue", """{"elementId":"fake:1"}""");

        Assert.Equal("text-of-fake:1", result);
        Assert.Equal(["GetText:fake:1"], _fake.Calls);
    }

    [Fact]
    public void NotOverriddenCapability_ThrowsSdkDefaultNotSupported()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            Execute("scrollElementIntoView", """{"elementId":"fake:1"}"""));

        Assert.Contains("'fake' tree provider does not support ScrollIntoView", ex.Message);
    }

    [Fact]
    public void UiaOnlyCommand_ProviderElement_RejectedWithoutCallingProvider()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            Execute("maximizeWindow", """{"elementId":"fake:1"}"""));

        Assert.Contains("only supported for UI Automation elements", ex.Message);
        Assert.Empty(_fake.Calls);
    }

    [Fact]
    public void MissingElementId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Execute("collapseElement", "{}"));
    }

    [Fact]
    public void UnknownUiaElementId_ThrowsKeyNotFound()
    {
        Assert.Throws<KeyNotFoundException>(() =>
            Execute("collapseElement", """{"elementId":"42.1.2"}"""));
        Assert.Empty(_fake.Calls);
    }

    [Fact]
    public void UiaOnlyRoutes_AreExactlyTheExpectedSet()
    {
        var uiaOnly = _dispatcher.ElementRoutes.Where(r => !r.Value).Select(r => r.Key);

        Assert.Equal(ExpectedUiaOnly.Order(), uiaOnly.Order(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryProviderRoute_ReachesProvider()
    {
        foreach (var (method, servedByProvider) in _dispatcher.ElementRoutes)
        {
            if (!servedByProvider) continue;
            _fake.Calls.Clear();

            var value = method == "setElementValue" ? "\"x\"" : "1.5";
            var ex = Record.Exception(() => Execute(method,
                $$"""{"elementId":"fake:1","property":"Name","value":{{value}}}"""));

            var reached = _fake.Calls.Count > 0
                || ex is NotSupportedException nse && nse.Message.Contains("'fake' tree provider does not support");
            Assert.True(reached, $"'{method}' did not reach the provider (exception: {ex?.GetType().Name}: {ex?.Message}).");
        }
    }

    /// <summary>
    /// Records every call. Overrides only <see cref="ITreeProvider.Collapse"/> among the
    /// optional capabilities, so the rest exercise the SDK default bodies.
    /// </summary>
    private sealed class FakeTreeProvider : ITreeProvider
    {
        public List<string> Calls { get; } = new();

        private object? Record(string op, string id, object? result = null)
        {
            Calls.Add($"{op}:{id}");
            return result;
        }

        public string Name => "fake";
        public IReadOnlyList<string> ElementIdPrefixes { get; } = ["fake:"];
        public bool OwnsElementId(string elementId) => elementId.StartsWith("fake:", StringComparison.Ordinal);
        public bool IsAttached => true;
        public bool OwnsWindow(IntPtr hwnd, string windowTitle) => false;
        public string? GetWindowRootId(IntPtr hwnd, string windowTitle) => null;
        public bool AutoRouteStandardFind => false;
        public bool AutoSwapsPageSource => false;

        public string? FindFirst(string rootElementId, ConditionDto condition, string scope) => null;
        public IReadOnlyList<string> FindAll(string rootElementId, ConditionDto condition, string scope) => [];
        public object? EvaluateXPath(string rootElementId, string expression, bool multiple) => null;

        public object? GetProperty(string elementId, string propertyName) => Record(nameof(GetProperty), elementId, "");
        public string GetText(string elementId) => (string)Record(nameof(GetText), elementId, $"text-of-{elementId}")!;
        public string GetTagName(string elementId) => (string)Record(nameof(GetTagName), elementId, "Button")!;
        public object GetRect(string elementId) =>
            Record(nameof(GetRect), elementId, new { x = 0.0, y = 0.0, width = 1.0, height = 1.0 })!;
        public string GetToggleState(string elementId) => (string)Record(nameof(GetToggleState), elementId, "Off")!;
        public bool IsSelected(string elementId) => (bool)Record(nameof(IsSelected), elementId, false)!;
        public bool IsAlive(string elementId) => true;

        public void Invoke(string elementId) => Record(nameof(Invoke), elementId);
        public void SetValue(string elementId, string value) => Record(nameof(SetValue), elementId);
        public void Select(string elementId) => Record(nameof(Select), elementId);
        public void RequestFocus(string elementId) => Record(nameof(RequestFocus), elementId);
        public void Expand(string elementId) => Record(nameof(Expand), elementId);
        public void Collapse(string elementId) => Record(nameof(Collapse), elementId);

        public void BuildPageSourceXml(string rootElementId, XmlDocument doc, XmlElement? parent) { }

        public void Dispose() { }
    }
}
