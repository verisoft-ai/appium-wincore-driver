using System.Text.Json;
using WincoreServer.Server;
using WincoreServer.State;
using WincoreServer.Uia3;

namespace WincoreServer.Commands;

public static class FindCommands
{
    public static object? FindElement(SessionState state, JsonElement? parameters)
    {
        var p = parameters ?? throw new ArgumentException("Parameters required.");
        var scope = p.GetProperty("scope").GetString() ?? "descendants";
        var conditionDto = JsonSerializer.Deserialize<ConditionDto>(p.GetProperty("condition").GetRawText())
            ?? throw new ArgumentException("condition is required.");

        string? contextElementId = null;
        if (p.TryGetProperty("contextElementId", out var ctxProp) && ctxProp.ValueKind == JsonValueKind.String)
        {
            contextElementId = ctxProp.GetString();
        }

        // Route to a tree provider when the context is already one of its elements,
        // or the search root is a window it auto-routes (Java windows). Opt-in-only
        // providers (.NET bridge) are reached through their own *ViaDotnetBridge
        // commands, never automatically from here.
        if (TryRouteToProvider(state, contextElementId, out var provider, out var providerRootId))
        {
            return provider.FindFirst(providerRootId, conditionDto, scope);
        }

        // When searching from the session root we re-resolve the attached HWND
        // via IUIAutomation.ElementFromHandle(hwnd) on every call. WPF apps
        // routinely rebuild their automation-peer tree after navigation (splash
        // → main, logout → login), invalidating any cached IUIAutomationElement.
        // Fresh resolution is a sub-ms COM call and gives us the live tree.
        var searchRoot = contextElementId != null
            ? state.GetElement(contextElementId)
            : (state.GetLiveRoot() ?? state.Automation.GetRootElement());

        var condition = ConditionBuilder.Build(state.Automation, conditionDto);

        switch (scope.ToLowerInvariant())
        {
            case "descendants":
                return FindFirstRecursively(searchRoot, condition, state, includeSelf: false);
            case "children":
            {
                var el = searchRoot.FindFirst(TreeScope.Children, condition);
                return el != null ? state.SaveElementAndReturnId(el) : null;
            }
            case "element":
            {
                var el = searchRoot.FindFirst(TreeScope.Element, condition);
                return el != null ? state.SaveElementAndReturnId(el) : null;
            }
            case "subtree":
                return FindFirstRecursively(searchRoot, condition, state, includeSelf: true);
            case "ancestors":
                return FindFirstAncestor(searchRoot, condition, state);
            case "ancestors-or-self":
                return FindFirstAncestorOrSelf(searchRoot, condition, state);
            case "parent":
                return FindParent(searchRoot, condition, state);
            case "following":
                return FindFollowing(searchRoot, condition, state);
            case "following-sibling":
                return FindFollowingSibling(searchRoot, condition, state);
            case "preceding":
                return FindPreceding(searchRoot, condition, state);
            case "preceding-sibling":
                return FindPrecedingSibling(searchRoot, condition, state);
            case "child-or-self":
            {
                var el = searchRoot.FindFirst(TreeScope.Element | TreeScope.Children, condition);
                return el != null ? state.SaveElementAndReturnId(el) : null;
            }
            default:
                throw new ArgumentException($"Unsupported scope: '{scope}'");
        }
    }

    public static object? FindElements(SessionState state, JsonElement? parameters)
    {
        var p = parameters ?? throw new ArgumentException("Parameters required.");
        var scope = p.GetProperty("scope").GetString() ?? "descendants";
        var conditionDto = JsonSerializer.Deserialize<ConditionDto>(p.GetProperty("condition").GetRawText())
            ?? throw new ArgumentException("condition is required.");

        string? contextElementId = null;
        if (p.TryGetProperty("contextElementId", out var ctxProp) && ctxProp.ValueKind == JsonValueKind.String)
        {
            contextElementId = ctxProp.GetString();
        }

        if (TryRouteToProvider(state, contextElementId, out var provider, out var providerRootId))
        {
            return provider.FindAll(providerRootId, conditionDto, scope);
        }

        // When searching from the session root we re-resolve the attached HWND
        // via IUIAutomation.ElementFromHandle(hwnd) on every call. WPF apps
        // routinely rebuild their automation-peer tree after navigation (splash
        // → main, logout → login), invalidating any cached IUIAutomationElement.
        // Fresh resolution is a sub-ms COM call and gives us the live tree.
        var searchRoot = contextElementId != null
            ? state.GetElement(contextElementId)
            : (state.GetLiveRoot() ?? state.Automation.GetRootElement());

        var condition = ConditionBuilder.Build(state.Automation, conditionDto);

        switch (scope.ToLowerInvariant())
        {
            case "descendants":
                return FindAllRecursively(searchRoot, condition, state, includeSelf: false);
            case "children":
                return SaveAll(searchRoot.FindAll(TreeScope.Children, condition), state);
            case "element":
                return SaveAll(searchRoot.FindAll(TreeScope.Element, condition), state);
            case "subtree":
                return FindAllRecursively(searchRoot, condition, state, includeSelf: true);
            case "ancestors":
                return FindAllAncestors(searchRoot, condition, state);
            case "ancestors-or-self":
                return FindAllAncestorsOrSelf(searchRoot, condition, state);
            case "parent":
            {
                var result = FindParent(searchRoot, condition, state);
                return result != null ? new[] { result } : Array.Empty<string>();
            }
            case "following":
                return FindAllFollowing(searchRoot, condition, state);
            case "following-sibling":
                return FindAllFollowingSiblings(searchRoot, condition, state);
            case "preceding":
                return FindAllPreceding(searchRoot, condition, state);
            case "preceding-sibling":
                return FindAllPrecedingSiblings(searchRoot, condition, state);
            case "child-or-self":
                return SaveAll(searchRoot.FindAll(TreeScope.Element | TreeScope.Children, condition), state);
            default:
                throw new ArgumentException($"Unsupported scope: '{scope}'");
        }
    }

    public static object? FindElementFocused(SessionState state, JsonElement? parameters)
    {
        var focused = state.Automation.GetFocusedElement();
        return state.SaveElementAndReturnId(focused);
    }

    public static object? SaveRootElementToTable(SessionState state, JsonElement? parameters)
    {
        var root = state.GetLiveRoot() ?? state.Automation.GetRootElement();
        return state.SaveElementAndReturnId(root);
    }

    public static object? LookupElement(SessionState state, JsonElement? parameters)
    {
        var p = parameters ?? throw new ArgumentException("Parameters required.");
        var elementId = p.GetProperty("elementId").GetString()
            ?? throw new ArgumentException("elementId is required.");

        if (state.Providers.TryResolve(elementId, out var provider))
        {
            try { return provider.IsAlive(elementId); }
            catch { return false; }
        }

        return state.ElementTable.ContainsKey(elementId);
    }

    // --- Ancestor / Following / Preceding via TreeWalker ---

    private static IUIAutomationTreeWalker DefaultWalker(SessionState state)
        => state.TreeWalker ?? state.Automation.ControlViewWalker;

    private static string? FindFirstAncestor(IUIAutomationElement element, IUIAutomationCondition condition, SessionState state)
    {
        var walker = DefaultWalker(state);
        var el = walker.GetParentElement(element);
        while (el != null)
        {
            if (el.FindFirst(TreeScope.Element, condition) != null)
            {
                return state.SaveElementAndReturnId(el);
            }
            el = walker.GetParentElement(el);
        }
        return null;
    }

    private static string? FindFirstAncestorOrSelf(IUIAutomationElement element, IUIAutomationCondition condition, SessionState state)
    {
        var walker = DefaultWalker(state);
        var el = element;
        while (el != null)
        {
            if (el.FindFirst(TreeScope.Element, condition) != null)
            {
                return state.SaveElementAndReturnId(el);
            }
            el = walker.GetParentElement(el);
        }
        return null;
    }

    private static string[] FindAllAncestors(IUIAutomationElement element, IUIAutomationCondition condition, SessionState state)
    {
        var walker = DefaultWalker(state);
        var results = new List<string>();
        var el = walker.GetParentElement(element);
        while (el != null)
        {
            if (el.FindFirst(TreeScope.Element, condition) != null)
            {
                results.Add(state.SaveElementAndReturnId(el));
            }
            el = walker.GetParentElement(el);
        }
        return results.ToArray();
    }

    private static string[] FindAllAncestorsOrSelf(IUIAutomationElement element, IUIAutomationCondition condition, SessionState state)
    {
        var walker = DefaultWalker(state);
        var results = new List<string>();
        var el = element;
        while (el != null)
        {
            if (el.FindFirst(TreeScope.Element, condition) != null)
            {
                results.Add(state.SaveElementAndReturnId(el));
            }
            el = walker.GetParentElement(el);
        }
        return results.ToArray();
    }

    private static string? FindParent(IUIAutomationElement element, IUIAutomationCondition condition, SessionState state)
    {
        var parent = DefaultWalker(state).GetParentElement(element);
        if (parent == null) return null;
        return parent.FindFirst(TreeScope.Element, condition) != null
            ? state.SaveElementAndReturnId(parent)
            : null;
    }

    private static string? FindFollowing(IUIAutomationElement element, IUIAutomationCondition condition, SessionState state)
    {
        var walker = DefaultWalker(state);
        var el = element;
        while (el != null)
        {
            var next = walker.GetNextSiblingElement(el);
            if (next != null)
            {
                if (next.FindFirst(TreeScope.Element, condition) != null)
                {
                    return state.SaveElementAndReturnId(next);
                }
                // Descend into this sibling's subtree to look for matches there first.
                var found = next.FindFirst(TreeScope.Descendants, condition);
                if (found != null)
                {
                    return state.SaveElementAndReturnId(found);
                }
                el = next;
                continue;
            }
            el = walker.GetParentElement(el);
        }
        return null;
    }

    private static string[] FindAllFollowing(IUIAutomationElement element, IUIAutomationCondition condition, SessionState state)
    {
        var walker = DefaultWalker(state);
        var results = new List<string>();
        var el = element;
        while (el != null)
        {
            var next = walker.GetNextSiblingElement(el);
            if (next != null)
            {
                el = next;
                if (el.FindFirst(TreeScope.Element, condition) != null)
                {
                    var id = state.TrySaveElementAndReturnId(el);
                    if (id != null) results.Add(id);
                }
                foreach (var match in IterateArray(el.FindAll(TreeScope.Descendants, condition)))
                {
                    var id = state.TrySaveElementAndReturnId(match);
                    if (id != null) results.Add(id);
                }
            }
            else
            {
                el = walker.GetParentElement(el);
            }
        }
        return results.ToArray();
    }

    private static string? FindFollowingSibling(IUIAutomationElement element, IUIAutomationCondition condition, SessionState state)
    {
        var walker = DefaultWalker(state);
        var el = element;
        while (true)
        {
            var next = walker.GetNextSiblingElement(el);
            if (next == null) break;
            el = next;
            if (el.FindFirst(TreeScope.Element, condition) != null)
            {
                return state.SaveElementAndReturnId(el);
            }
        }
        return null;
    }

    private static string[] FindAllFollowingSiblings(IUIAutomationElement element, IUIAutomationCondition condition, SessionState state)
    {
        var walker = DefaultWalker(state);
        var results = new List<string>();
        var el = element;
        while (true)
        {
            var next = walker.GetNextSiblingElement(el);
            if (next == null) break;
            el = next;
            if (el.FindFirst(TreeScope.Element, condition) != null)
            {
                results.Add(state.SaveElementAndReturnId(el));
            }
        }
        return results.ToArray();
    }

    private static string? FindPreceding(IUIAutomationElement element, IUIAutomationCondition condition, SessionState state)
    {
        var walker = DefaultWalker(state);
        var el = element;
        while (el != null)
        {
            var prev = walker.GetPreviousSiblingElement(el);
            if (prev != null)
            {
                if (prev.FindFirst(TreeScope.Element, condition) != null)
                {
                    return state.SaveElementAndReturnId(prev);
                }
                var found = prev.FindFirst(TreeScope.Descendants, condition);
                if (found != null)
                {
                    return state.SaveElementAndReturnId(found);
                }
                el = prev;
                continue;
            }
            el = walker.GetParentElement(el);
        }
        return null;
    }

    private static string[] FindAllPreceding(IUIAutomationElement element, IUIAutomationCondition condition, SessionState state)
    {
        var walker = DefaultWalker(state);
        var results = new List<string>();
        var el = element;
        while (el != null)
        {
            var prev = walker.GetPreviousSiblingElement(el);
            if (prev != null)
            {
                el = prev;
                if (el.FindFirst(TreeScope.Element, condition) != null)
                {
                    var id = state.TrySaveElementAndReturnId(el);
                    if (id != null) results.Add(id);
                }
                foreach (var match in IterateArray(el.FindAll(TreeScope.Descendants, condition)))
                {
                    var id = state.TrySaveElementAndReturnId(match);
                    if (id != null) results.Add(id);
                }
            }
            else
            {
                el = walker.GetParentElement(el);
            }
        }
        return results.ToArray();
    }

    private static string? FindPrecedingSibling(IUIAutomationElement element, IUIAutomationCondition condition, SessionState state)
    {
        var walker = DefaultWalker(state);
        var el = element;
        while (true)
        {
            var prev = walker.GetPreviousSiblingElement(el);
            if (prev == null) break;
            el = prev;
            if (el.FindFirst(TreeScope.Element, condition) != null)
            {
                return state.SaveElementAndReturnId(el);
            }
        }
        return null;
    }

    private static string[] FindAllPrecedingSiblings(IUIAutomationElement element, IUIAutomationCondition condition, SessionState state)
    {
        var walker = DefaultWalker(state);
        var results = new List<string>();
        var el = element;
        while (true)
        {
            var prev = walker.GetPreviousSiblingElement(el);
            if (prev == null) break;
            el = prev;
            if (el.FindFirst(TreeScope.Element, condition) != null)
            {
                results.Add(state.SaveElementAndReturnId(el));
            }
        }
        return results.ToArray();
    }

    // ── tree-provider routing ────────────────────────────────────────────────────

    /// <summary>
    /// Decides whether a find should be served from a tree provider (Java agent,
    /// .NET bridge) instead of real UIA, and if so resolves the provider's own root
    /// element id to search from.
    ///
    /// Routes when: the context is already one of a provider's elements (continuing
    /// a search inside that tree); or a fresh find whose window a provider both
    /// <see cref="ITreeProvider.OwnsWindow"/>s and <see cref="ITreeProvider.AutoRouteStandardFind"/>s
    /// (Java — a Java window's children live only in the agent tree). Opt-in-only
    /// providers such as the .NET bridge are never auto-routed here.
    /// </summary>
    internal static bool TryRouteToProvider(
        SessionState state, string? contextElementId, out ITreeProvider provider, out string providerRootId)
    {
        provider = null!;
        providerRootId = null!;

        if (contextElementId != null && state.Providers.TryResolve(contextElementId, out var byId))
        {
            provider = byId;
            providerRootId = contextElementId;
            return true;
        }

        IntPtr hwnd;
        string title;
        if (contextElementId != null)
        {
            IUIAutomationElement uiaRoot;
            try { uiaRoot = state.GetElement(contextElementId); }
            catch { return false; }
            hwnd = uiaRoot.CurrentNativeWindowHandle;
            title = uiaRoot.get_CurrentName() ?? "";
        }
        else
        {
            var live = state.GetLiveRoot();
            if (live == null) return false;
            hwnd = live.CurrentNativeWindowHandle;
            title = live.get_CurrentName() ?? "";
        }

        if (hwnd == IntPtr.Zero) return false;
        if (!state.Providers.TryResolveWindow(hwnd, title, out var windowProvider)) return false;
        if (!windowProvider.AutoRouteStandardFind) return false;

        var rootId = windowProvider.GetWindowRootId(hwnd, title);
        if (rootId == null) return false;

        provider = windowProvider;
        providerRootId = rootId;
        return true;
    }

    // Descendant / subtree search is UIA's own scoped FindFirst/FindAll and nothing
    // more. A previous implementation supplemented this with a manual child-by-child
    // walk via TreeScope.Children to recover matches native scope skips (WPF popups
    // hosted in a separate fragment root, virtualised lists). That walk was removed:
    // it ran unconditionally even when native returned complete results, had no
    // containment guard, and on legacy Win32 providers (ComboBox, old ActiveX) whose
    // TreeScope.Children navigation is broken it escaped the element's subtree and
    // enumerated the whole desktop (20k+ elements for `.//*`). Provider-boundary
    // cases that genuinely need crossing (IE/MSHTML documents, some popups) should be
    // handled with targeted fragment-root resolution, not a blanket re-walk.

    private static string? FindFirstRecursively(IUIAutomationElement element, IUIAutomationCondition condition, SessionState state, bool includeSelf)
    {
        var scope = includeSelf ? TreeScope.Subtree : TreeScope.Descendants;
        var native = element.FindFirst(scope, condition);
        if (native != null) return state.TrySaveElementAndReturnId(native);

        if (includeSelf)
        {
            var self = element.FindFirst(TreeScope.Element, condition);
            if (self != null) return state.TrySaveElementAndReturnId(self);
        }
        return null;
    }

    private static string[] FindAllRecursively(IUIAutomationElement element, IUIAutomationCondition condition, SessionState state, bool includeSelf)
    {
        var scope = includeSelf ? TreeScope.Subtree : TreeScope.Descendants;
        var results = IterateArray(element.FindAll(scope, condition))
            .Select(el => state.TrySaveElementAndReturnId(el))
            .Where(id => id != null)
            .Select(id => id!)
            .ToList();

        // TreeScope.Subtree already includes the element itself, but a broken provider
        // can omit it from FindAll while still matching it via TreeScope.Element.
        // Prepend it: descendant-or-self is document order, so self comes first.
        if (includeSelf)
        {
            var self = element.FindFirst(TreeScope.Element, condition);
            if (self != null)
            {
                var id = state.TrySaveElementAndReturnId(self);
                if (id != null && !results.Contains(id)) results.Insert(0, id);
            }
        }
        return results.ToArray();
    }

    // --- UIA3 array iteration ---

    public static IEnumerable<IUIAutomationElement> IterateArray(IUIAutomationElementArray? array)
    {
        if (array == null) yield break;
        var len = array.Length;
        for (var i = 0; i < len; i++)
        {
            yield return array.GetElement(i);
        }
    }

    private static string[] SaveAll(IUIAutomationElementArray array, SessionState state)
    {
        var results = new List<string>();
        foreach (var el in IterateArray(array))
        {
            var id = state.TrySaveElementAndReturnId(el);
            if (id != null) results.Add(id);
        }
        return results.ToArray();
    }
}
