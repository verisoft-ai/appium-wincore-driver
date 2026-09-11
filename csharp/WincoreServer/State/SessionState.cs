using WincoreServer.Diagnostics;
using WincoreServer.Plugins;
using WincoreServer.Uia3;
using Wincore.ServerSdk;

namespace WincoreServer.State;

public class SessionState : ISessionContext
{
    // One CUIAutomation instance per session. COM reference-counted by the CLR.
    // All IUIAutomationElement / IUIAutomationCondition instances we create go
    // through this object.
    public readonly IUIAutomation Automation = Uia3Factory.Create();

    public Dictionary<string, IUIAutomationElement> ElementTable { get; } = new();
    public IUIAutomationElement? RootElement { get; private set; }
    public IUIAutomationCacheRequest? CacheRequest { get; set; }
    public IUIAutomationTreeWalker? TreeWalker { get; set; }

    // Performance counters — opt-in via the perfMetrics capability (passed on init).
    // Always allocated (cheap); only written to when PerfMetricsEnabled is true.
    public bool PerfMetricsEnabled { get; set; }
    public PerfCounters Perf { get; } = new();

    /// <summary>Tree providers contributed by loaded plugins (Java agent, .NET bridge, …).</summary>
    public ProviderRegistry Providers { get; } = new();

    // ── ISessionContext (the narrowed view handed to plugins / their providers) ──

    IntPtr ISessionContext.GetLiveRootHandle() => RootNativeWindowHandle;

    string ISessionContext.GetLiveRootName()
    {
        try { return GetLiveRoot()?.get_CurrentName() ?? ""; }
        catch { return ""; }
    }

    bool ISessionContext.PerfEnabled => PerfMetricsEnabled;

    IPerfSink ISessionContext.Perf => PerfMetricsEnabled ? Perf : NullPerfSink.Instance;

    void ISessionContext.LogInfo(string message) => Console.Error.WriteLine(message);

    void ISessionContext.LogError(string message) => Console.Error.WriteLine(message);

    /// <summary>
    /// PID of the process most recently launched on this session's behalf
    /// (Appium <c>app</c> capability / <c>startProcess</c>). Default attach target
    /// for a tree-provider plugin given no explicit pid/hwnd.
    /// </summary>
    public int LastStartedProcessId { get; set; }

    // HWND of the attached top-level window (0 if the root is the desktop or
    // an element with no native handle). We re-resolve the root via
    // IUIAutomation.ElementFromHandle(hwnd) per find so that WPF apps which
    // rebuild their automation-peer tree after a splash→main navigation (same
    // HWND, different UIA children) are seen fresh. UIA3's ElementFromHandle
    // is a sub-ms COM call, so this is cheap.
    public IntPtr RootNativeWindowHandle { get; private set; } = IntPtr.Zero;

    public void SetRoot(IUIAutomationElement? element)
    {
        RootElement = element;
        RootNativeWindowHandle = element?.CurrentNativeWindowHandle ?? IntPtr.Zero;
    }

    /// <summary>
    /// Returns a live IUIAutomationElement for the attached root. Prefers a
    /// fresh ElementFromHandle(hwnd) lookup so that we see the current UIA
    /// tree, not a cached automation peer that the target app may have torn
    /// down (e.g. WPF Frame.Navigate, splash→main swap). Falls back to the
    /// stored element if no HWND is tracked (desktop root, certain UWP hosts).
    /// </summary>
    public IUIAutomationElement? GetLiveRoot()
    {
        if (RootNativeWindowHandle != IntPtr.Zero)
        {
            try
            {
                var fresh = Automation.ElementFromHandle(RootNativeWindowHandle);
                if (fresh != null) return fresh;
            }
            catch
            {
                // HWND invalidated — fall through to cached reference.
            }
        }
        return RootElement;
    }

    public string SaveElementAndReturnId(IUIAutomationElement element)
    {
        var runtimeId = element.GetRuntimeId();
        var id = runtimeId == null || runtimeId.Length == 0
            ? Guid.NewGuid().ToString()
            : string.Join(".", runtimeId);

        if (!ElementTable.ContainsKey(id))
        {
            ElementTable[id] = element;
        }

        return id;
    }

    public string? TrySaveElementAndReturnId(IUIAutomationElement element)
    {
        try
        {
            var runtimeId = element.GetRuntimeId();
            var id = runtimeId == null || runtimeId.Length == 0
                ? Guid.NewGuid().ToString()
                : string.Join(".", runtimeId);
            if (!ElementTable.ContainsKey(id))
                ElementTable[id] = element;
            return id;
        }
        catch
        {
            return null;
        }
    }

    public IUIAutomationElement GetElement(string elementId)
    {
        if (ElementTable.TryGetValue(elementId, out var element))
        {
            return element;
        }

        throw new KeyNotFoundException($"Element with ID '{elementId}' not found in element table.");
    }

    public IUIAutomationElement GetRootOrThrow()
    {
        return RootElement ?? throw new InvalidOperationException("Root element is not set.");
    }

    public void Initialize()
    {
        // UIA3 cache requests are passed per-call (not pushed thread-locally like
        // UIA1). We build a default request filtering out Chrome-hosted elements
        // (matches the historical PowerShell behaviour) but leave it null — Find
        // commands don't pass it today. If profiling shows it's a win we'll wire
        // it through FindFirstBuildCache.
        CacheRequest = Automation.CreateCacheRequest();
        var chromeFilter = Automation.CreatePropertyCondition(UIA.FrameworkIdPropertyId, "Chrome");
        CacheRequest.TreeFilter = Automation.CreateAndCondition(
            Automation.ControlViewCondition,
            Automation.CreateNotCondition(chromeFilter));

        TreeWalker = Automation.CreateTreeWalker(CacheRequest.TreeFilter);
    }

    public void Dispose()
    {
        ElementTable.Clear();
        SetRoot(null);
        CacheRequest = null;
        TreeWalker = null;
        Providers.Dispose();
        LastStartedProcessId = 0;
    }
}
