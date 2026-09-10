using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml;
using DesktopDriverServer.Java;
using Wincore.ServerSdk;

namespace DesktopDriverServer.Plugins.BuiltIn;

/// <summary>
/// Built-in server plugin for the Java Access Bridge (Swing / AWT) agent. Kept
/// in-tree for now; a later migration step moves this to the standalone
/// <c>appium-wincore-java-bridge</c> repo, loaded by the same
/// <see cref="PluginLoader"/> from <c>DESKTOP_DRIVER_PLUGINS</c>. The wire surface
/// (element-id prefix <c>java:</c>, the <c>enableJavaSwing</c> / <c>injectJavaAgent</c>
/// commands, auto-routing on Java windows) is unchanged.
/// </summary>
public sealed class JavaBridgePlugin : IServerPlugin
{
    private JavaTreeProvider? _provider;

    public string Name => "java-bridge";
    public string SdkVersion => SdkContract.Version;

    public ITreeProvider CreateProvider(ISessionContext context)
    {
        _provider = new JavaTreeProvider(context);
        return _provider;
    }

    public IReadOnlyDictionary<string, PluginCommandHandler> GetCommands() => new Dictionary<string, PluginCommandHandler>
    {
        ["enableJavaSwing"] = EnableJavaSwing,
        ["injectJavaAgent"] = InjectJavaAgent,
    };

    private JavaTreeProvider Provider =>
        _provider ?? throw new InvalidOperationException("Java provider not created for this session.");

    private object? EnableJavaSwing(ISessionContext ctx, JsonElement? parameters)
    {
        int? pid = null;
        if (parameters?.TryGetProperty("pid", out var pidEl) == true && pidEl.ValueKind == JsonValueKind.Number)
            pid = pidEl.GetInt32();

        int targetPid = pid ?? ctx.LastStartedProcessId;
        if (targetPid == 0)
            throw new InvalidOperationException(
                "No process PID available. Use appTopLevelWindow with javaSwing:true, or launch the app via Appium.");

        Provider.Connect(targetPid);
        return null;
    }

    private object? InjectJavaAgent(ISessionContext ctx, JsonElement? parameters)
    {
        IntPtr hwnd;
        if (parameters?.TryGetProperty("hwnd", out var hwndEl) == true && hwndEl.ValueKind == JsonValueKind.Number)
        {
            hwnd = (IntPtr)hwndEl.GetInt64();
        }
        else
        {
            hwnd = ctx.GetLiveRootHandle();
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException(
                    "Current root element has no native window handle. " +
                    "Attach to a Java window first using appTopLevelWindow capability.");
        }

        string? jdkPath = null;
        if (parameters?.TryGetProperty("jdkPath", out var jdkPathEl) == true && jdkPathEl.ValueKind == JsonValueKind.String)
            jdkPath = jdkPathEl.GetString();

        string agentJar = Path.Combine(AppContext.BaseDirectory, "appium-desktop-agent.jar");
        if (!File.Exists(agentJar))
            throw new FileNotFoundException($"Agent JAR not found at: {agentJar}");

        try
        {
            AgentInjector.InjectFromHwnd(hwnd, agentJar, jdkPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(BuildDiagnosticMessage(ex, hwnd, jdkPath), ex);
        }

        AgentInjector.GetWindowThreadProcessId(hwnd, out uint pid);
        Provider.Connect((int)pid);
        return null;
    }

    private static string BuildDiagnosticMessage(Exception ex, IntPtr hwnd, string? jdkPath = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(ex.Message);
        sb.AppendLine();
        sb.AppendLine("=== attachJavaSwing diagnostics ===");

        AgentInjector.GetWindowThreadProcessId(hwnd, out uint pid);
        sb.AppendLine($"  hwnd:        0x{hwnd:X}");
        sb.AppendLine($"  pid:         {pid}");

        if (pid != 0)
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                sb.AppendLine($"  process:     {process.ProcessName}.exe");
                sb.AppendLine($"  exe:         {process.MainModule?.FileName ?? "(unavailable)"}");

                bool hasJvm = false;
                var jvmPath = "(not found)";
                try
                {
                    foreach (ProcessModule m in process.Modules)
                    {
                        if (m.ModuleName.Contains("jvm", StringComparison.OrdinalIgnoreCase))
                        {
                            hasJvm = true;
                            jvmPath = m.FileName;
                            break;
                        }
                    }
                }
                catch { jvmPath = "(module enumeration denied)"; }

                sb.AppendLine($"  jvm.dll:     {(hasJvm ? jvmPath : "(not loaded — is this a Java process?)")}");
            }
            catch (Exception procEx)
            {
                sb.AppendLine($"  process:     (could not open: {procEx.Message})");
            }
        }

        sb.AppendLine();
        if (jdkPath != null)
            sb.AppendLine("  jdkPath (explicit): " + jdkPath);
        sb.AppendLine("  JAVA_HOME:          " + (Environment.GetEnvironmentVariable("JAVA_HOME") ?? "(not set)"));

        try
        {
            var resolvedJava = AgentInjector.FindJavaExe(jdkPath);
            var bitness = resolvedJava.Contains("Program Files (x86)", StringComparison.OrdinalIgnoreCase)
                ? "32-bit (x86)"
                : "64-bit or unknown";
            sb.AppendLine($"  java.exe used:      {resolvedJava} [{bitness}]");
        }
        catch (Exception jex)
        {
            sb.AppendLine($"  java.exe used:      (resolution failed: {jex.Message})");
        }
        sb.AppendLine("  JAVA_TOOL_OPTIONS:  " + (Environment.GetEnvironmentVariable("JAVA_TOOL_OPTIONS") ?? "(not set)"));
        sb.AppendLine("  _JAVA_OPTIONS:      " + (Environment.GetEnvironmentVariable("_JAVA_OPTIONS") ?? "(not set)"));
        sb.AppendLine("  JDK_JAVA_OPTIONS:   " + (Environment.GetEnvironmentVariable("JDK_JAVA_OPTIONS") ?? "(not set)"));

        sb.AppendLine("===================================");
        return sb.ToString();
    }
}

/// <summary>
/// <see cref="ITreeProvider"/> over <see cref="JavaAgentService"/>. All the
/// runtime-specific quirks (deriving ExpandCollapseState / HasKeyboardFocus from
/// the AccessibleState list, live re-fetch of volatile properties, toggle/selection
/// state from the state string) live here — the host's command handlers just call
/// the interface.
/// </summary>
internal sealed class JavaTreeProvider : ITreeProvider
{
    private static readonly string[] Prefixes = { "java:" };

    private readonly ISessionContext _ctx;
    private readonly JavaAgentService _svc = new();
    private bool _attached;

    public JavaTreeProvider(ISessionContext ctx) => _ctx = ctx;

    public void Connect(int pid)
    {
        _svc.Perf = _ctx.PerfEnabled ? _ctx.Perf : null;
        _svc.Connect(pid);
        _attached = true;
        _ctx.LogInfo("[java-bridge] connected to JVM pid " + pid);
    }

    public string Name => "java";
    public IReadOnlyList<string> ElementIdPrefixes => Prefixes;
    public bool OwnsElementId(string elementId) => JavaAgentElement.IsJavaId(elementId);
    public bool IsAttached => _attached;

    public bool AutoRouteStandardFind => true;
    public bool AutoSwapsPageSource => true;

    public bool OwnsWindow(IntPtr hwnd, string windowTitle)
        => _attached && hwnd != IntPtr.Zero && JavaWindowDetector.IsJavaWindow(hwnd);

    public string? GetWindowRootId(IntPtr hwnd, string windowTitle)
        => _svc.GetWindowRoot(hwnd, windowTitle)?.Id;

    public string? FindFirst(string rootElementId, ConditionDto condition, string scope)
        => _svc.FindFirst(_svc.GetById(rootElementId), condition, scope);

    public IReadOnlyList<string> FindAll(string rootElementId, ConditionDto condition, string scope)
        => _svc.FindAll(_svc.GetById(rootElementId), condition, scope);

    public object? EvaluateXPath(string rootElementId, string expression, bool multiple)
        => _svc.EvaluateXPath(_svc.GetById(rootElementId), expression, multiple);

    public object? GetProperty(string elementId, string propertyName)
    {
        var el = _svc.GetById(elementId);
        var lowerProp = propertyName.ToLowerInvariant();
        if (lowerProp is "isenabled" or "isoffscreen" or "haskeyboardfocus" or "iskeyboardfocusable" or "clickablepoint" or "states")
        {
            _svc.GetFreshInfo(el);
        }

        // JAB has no ExpandCollapseState property — expansion is reported via the
        // AccessibleState list (same shape GetToggleState reads). A literal key lookup
        // always misses and returns "", which patternExpand's isExpanded() (extension.ts)
        // reads as a confirmed "not expanded" rather than "can't verify".
        if (propertyName.Equals("ExpandCollapseState", StringComparison.OrdinalIgnoreCase))
        {
            _svc.GetFreshInfo(el);
            var states = _svc.GetProperty(el, "States")?.ToString() ?? "";
            if (states.Contains("expanded", StringComparison.OrdinalIgnoreCase)) return "Expanded";
            if (states.Contains("collapsed", StringComparison.OrdinalIgnoreCase)) return "Collapsed";
            return "";
        }

        // JAB has no HasKeyboardFocus property either — reported via the state list.
        if (propertyName.Equals("HasKeyboardFocus", StringComparison.OrdinalIgnoreCase))
        {
            _svc.GetFreshInfo(el);
            var states = _svc.GetProperty(el, "States")?.ToString() ?? "";
            return states.Contains("focused", StringComparison.OrdinalIgnoreCase);
        }

        return _svc.GetProperty(el, propertyName);
    }

    public string GetText(string elementId) => _svc.GetText(_svc.GetById(elementId));
    public string GetTagName(string elementId) => _svc.GetTagName(_svc.GetById(elementId));
    public object GetRect(string elementId) => _svc.GetRect(_svc.GetById(elementId));

    public string GetToggleState(string elementId)
    {
        var states = FreshStates(elementId);
        if (states.Contains("indeterminate", StringComparison.OrdinalIgnoreCase)) return "Indeterminate";
        return states.Contains("checked", StringComparison.OrdinalIgnoreCase) ? "On" : "Off";
    }

    public bool IsSelected(string elementId)
    {
        var states = FreshStates(elementId);
        return states.Contains("checked", StringComparison.OrdinalIgnoreCase)
            || states.Contains("selected", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsAlive(string elementId)
    {
        try { return _svc.IsAlive(elementId); }
        catch { return false; }
    }

    public void Invoke(string elementId) => _svc.Invoke(_svc.GetById(elementId));
    public void SetValue(string elementId, string value) => _svc.SetValue(_svc.GetById(elementId), value);
    public void Select(string elementId) => _svc.Select(_svc.GetById(elementId));
    public void RequestFocus(string elementId) => _svc.RequestFocus(_svc.GetById(elementId));
    public void Expand(string elementId) => _svc.Expand(_svc.GetById(elementId));

    public void BuildPageSourceXml(string rootElementId, XmlDocument doc, XmlElement? parent)
        => _svc.BuildXml(_svc.GetById(rootElementId), doc, parent);

    public void Dispose() => _svc.Dispose();

    // Java Info.States is stale after interaction — re-fetch live before reading it.
    private string FreshStates(string elementId)
    {
        var el = _svc.GetById(elementId);
        var info = _svc.GetFreshInfo(el) ?? el.Info;
        return info.TryGetValue("States", out var s) ? s?.ToString() ?? "" : "";
    }
}
