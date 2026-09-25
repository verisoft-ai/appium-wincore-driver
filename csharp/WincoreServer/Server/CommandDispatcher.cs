using System.Text.Json;
using WincoreServer.Commands;
using WincoreServer.Plugins;
using WincoreServer.State;

namespace WincoreServer.Server;

public class CommandDispatcher
{
    private readonly Dictionary<string, Func<SessionState, JsonElement?, object?>> _handlers;

    public CommandDispatcher(PluginHost? plugins = null)
    {
        _handlers = new Dictionary<string, Func<SessionState, JsonElement?, object?>>(StringComparer.OrdinalIgnoreCase)
        {
            // Session
            ["init"] = SessionCommands.Init,
            ["setRootElement"] = SessionCommands.SetRootElement,
            ["setRootElementNull"] = SessionCommands.SetRootElementNull,
            ["setRootElementFromHandle"] = SessionCommands.SetRootElementFromHandle,
            ["elementFromHandle"] = SessionCommands.ElementFromHandle,
            ["checkRootElementNotNull"] = SessionCommands.CheckRootElementNotNull,
            ["setCacheRequestTreeFilter"] = SessionCommands.SetCacheRequestTreeFilter,
            ["setCacheRequestTreeScope"] = SessionCommands.SetCacheRequestTreeScope,
            ["setCacheRequestAutomationElementMode"] = SessionCommands.SetCacheRequestAutomationElementMode,
            ["dispose"] = SessionCommands.Dispose,

            // Find
            ["findElement"] = FindCommands.FindElement,
            ["findElements"] = FindCommands.FindElements,
            ["findElementFocused"] = FindCommands.FindElementFocused,
            ["saveRootElementToTable"] = FindCommands.SaveRootElementToTable,
            ["lookupElement"] = FindCommands.LookupElement,
            ["evaluateXPath"] = XPathCommands.EvaluateXPath,

            // Element (non element-scoped; element-scoped commands are in AddElementRoutes)
            ["getRootRect"] = ElementCommands.GetRootRect,
            ["sendKeys"] = ElementCommands.SendKeys,

            // Accessibility (MSAA fallback). Not routed: it answers provider elements
            // with a structured "unsupported" result rather than an error.
            ["getAccessibleChildren"] = AccessibilityCommands.GetAccessibleChildren,

            // Page source & screenshots
            ["getPageSource"] = PageSourceCommands.GetPageSource,
            ["getScreenshot"] = ScreenshotCommands.GetScreenshot,

            // Clipboard
            ["getClipboardText"] = ClipboardCommands.GetClipboardText,
            ["setClipboardText"] = ClipboardCommands.SetClipboardText,
            ["getClipboardImage"] = ClipboardCommands.GetClipboardImage,
            ["setClipboardImage"] = ClipboardCommands.SetClipboardImage,

            // Process
            ["startProcess"] = ProcessCommands.StartProcess,
            ["getProcessIds"] = ProcessCommands.GetProcessIds,
            ["getChildProcessIds"] = ProcessCommands.GetChildProcessIds,
            ["stopProcess"] = ProcessCommands.StopProcess,
            ["executePowerShellScript"] = ProcessCommands.ExecutePowerShellScript,

            // File system
            ["deleteFile"] = FileSystemCommands.DeleteFile,
            ["deleteFolder"] = FileSystemCommands.DeleteFolder,

            // Tree-provider bridges (Java agent, .NET bridge) contribute their own
            // commands — enableJavaSwing / injectJavaAgent / injectDotnetBridge /
            // *ViaDotnetBridge — via IServerPlugin.GetCommands(); merged below.

            // Diagnostics
            ["getPerfMetrics"] = PerfCommands.GetPerfMetrics,
            ["resetPerfMetrics"] = PerfCommands.ResetPerfMetrics,
            ["getMonitors"] = DiagnosticCommands.GetMonitors,
            ["debug:ping"] = DiagnosticCommands.Ping,
            ["debug:inspectElementTable"] = DiagnosticCommands.InspectElementTable,
        };

        AddElementRoutes();

        if (plugins != null)
        {
            foreach (var (method, handler) in plugins.GetPluginCommands())
            {
                if (_handlers.ContainsKey(method))
                    throw new InvalidOperationException(
                        $"A plugin contributes command '{method}' which is already a core method.");
                _handlers[method] = handler;
            }
        }
    }

    /// <summary>
    /// Every command acting on an existing element id, routed through
    /// <see cref="ElementRoute"/>: tree-provider ids go to the provider side, UIA ids
    /// to the UIA side (with the element already resolved). A null provider side
    /// marks the command UIA-only.
    /// </summary>
    private void AddElementRoutes()
    {
        // Element
        AddElement("getProperty", ElementCommands.GetProperty,
            // The provider owns the UIA-property-name mapping and any per-property
            // freshness re-fetch.
            (tp, id, p) => tp.GetProperty(id, ElementCommands.ReadPropertyName(p)));
        AddElement("getTagName", ElementCommands.GetTagName, (tp, id, _) => tp.GetTagName(id));
        AddElement("getText", ElementCommands.GetText, (tp, id, _) => tp.GetText(id));
        AddElement("getRect", ElementCommands.GetRect, (tp, id, _) => tp.GetRect(id));
        AddElement("setFocus", ElementCommands.SetFocus, (tp, id, _) => { tp.RequestFocus(id); return null; });
        AddElement("setElementValue", ElementCommands.SetValue,
            (tp, id, p) => { tp.SetValue(id, ElementCommands.ReadValue(p)); return null; });
        AddElement("getElementValue", ElementCommands.GetValue, (tp, id, _) => tp.GetText(id));
        AddElement("getElementScreenshot", ScreenshotCommands.GetElementScreenshot,
            ScreenshotCommands.GetProviderElementScreenshot);

        // Patterns
        AddElement("invokeElement", PatternCommands.Invoke, (tp, id, _) => { tp.Invoke(id); return null; });
        // Java throws "JAB_NO_EXPAND_ACTION" when AccessibleAction is unavailable, and
        // an unsupported Collapse throws NotSupportedException — the TypeScript
        // patternExpand / patternCollapse catch both and fall back to ALT+Down.
        AddElement("expandElement", PatternCommands.Expand, (tp, id, _) => { tp.Expand(id); return null; });
        AddElement("collapseElement", PatternCommands.Collapse, (tp, id, _) => { tp.Collapse(id); return null; });
        // No provider has a TogglePattern equivalent — fire the default accessible
        // action (toggles checkboxes, buttons). Invoke applies the settle delay.
        AddElement("toggleElement", PatternCommands.Toggle, (tp, id, _) => { tp.Invoke(id); return null; });
        AddElement("getToggleState", PatternCommands.GetToggleState, (tp, id, _) => tp.GetToggleState(id));
        AddElement("setElementRangeValue", PatternCommands.SetRangeValue,
            (tp, id, p) => { tp.SetRangeValue(id, p.GetProperty("value").GetDouble()); return null; });
        AddElement("scrollElementIntoView", PatternCommands.ScrollIntoView, (tp, id, _) => { tp.ScrollIntoView(id); return null; });
        AddElement("selectElement", PatternCommands.Select, (tp, id, _) => { tp.Select(id); return null; });
        AddElement("addToSelection", PatternCommands.AddToSelection, (tp, id, _) => { tp.AddToSelection(id); return null; });
        AddElement("removeFromSelection", PatternCommands.RemoveFromSelection, (tp, id, _) => { tp.RemoveFromSelection(id); return null; });
        AddElement("isElementSelected", PatternCommands.IsSelected, (tp, id, _) => tp.IsSelected(id));
        AddElement("isMultipleSelect", PatternCommands.IsMultipleSelect, (tp, id, _) => tp.IsMultipleSelect(id));
        AddElement("getSelectedElements", PatternCommands.GetSelectedElements,
            (tp, id, _) => tp.GetSelectedElements(id).ToArray());

        // UIA-only: window operations are performed by Windows itself, never by a
        // tree provider; the session root is always a real UIA element.
        AddElement("maximizeWindow", PatternCommands.MaximizeWindow, provider: null);
        AddElement("minimizeWindow", PatternCommands.MinimizeWindow, provider: null);
        AddElement("restoreWindow", PatternCommands.RestoreWindow, provider: null);
        AddElement("closeWindow", PatternCommands.CloseWindow, provider: null);
        AddElement("moveWindow", PatternCommands.MoveWindow, provider: null);
        AddElement("resizeWindow", PatternCommands.ResizeWindow, provider: null);
        AddElement("setRootElementFromElementId", SessionCommands.SetRootElementFromElementId, provider: null);
    }

    private readonly Dictionary<string, bool> _elementRoutes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Element-scoped methods → whether a tree provider can serve them.</summary>
    internal IReadOnlyDictionary<string, bool> ElementRoutes => _elementRoutes;

    private void AddElement(string method, UiaElementHandler uia, ProviderElementHandler? provider)
    {
        _handlers.Add(method, ElementRoute.Create(method, uia, provider));
        _elementRoutes.Add(method, provider != null);
    }

    public bool HasHandler(string method) => _handlers.ContainsKey(method);

    public object? Execute(string method, SessionState state, JsonElement? parameters)
    {
        if (!_handlers.TryGetValue(method, out var handler))
        {
            throw new ArgumentException($"Unknown method: '{method}'");
        }

        // Handlers run inline on the request-loop STA thread. UIA3 RPC calls
        // don't deadlock the way the UIA1 managed wrapper did, so we don't need
        // per-command worker threads or timeouts.
        return handler(state, parameters);
    }
}
