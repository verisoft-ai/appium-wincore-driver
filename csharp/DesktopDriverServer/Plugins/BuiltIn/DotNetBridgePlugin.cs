using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml;
using DesktopDriverServer.DotNet;
using Wincore.ServerSdk;

namespace DesktopDriverServer.Plugins.BuiltIn;

/// <summary>
/// Built-in server plugin for the .NET (WinForms / WPF / DevExpress) bridge. Kept
/// in-tree for now; a later migration step moves this to the standalone
/// <c>appium-wincore-dotnet-bridge</c> repo. Unlike the Java plugin the bridge tree
/// is never auto-merged — <see cref="DotNetTreeProvider.AutoRouteStandardFind"/> and
/// <see cref="DotNetTreeProvider.AutoSwapsPageSource"/> are false — so this plugin
/// also contributes the explicit <c>*ViaDotnetBridge</c> command family.
/// </summary>
public sealed class DotNetBridgePlugin : IServerPlugin
{
    private DotNetTreeProvider? _provider;

    public string Name => "dotnet-bridge";
    public string SdkVersion => SdkContract.Version;

    public ITreeProvider CreateProvider(ISessionContext context)
    {
        _provider = new DotNetTreeProvider(context);
        return _provider;
    }

    public IReadOnlyDictionary<string, PluginCommandHandler> GetCommands() => new Dictionary<string, PluginCommandHandler>
    {
        ["enableDotnetBridge"] = EnableDotnetBridge,
        ["injectDotnetBridge"] = InjectDotnetBridge,
        ["findElementDotnetBridge"] = FindElementDotnetBridge,
        ["findElementsDotnetBridge"] = FindElementsDotnetBridge,
        ["evaluateXPathDotnetBridge"] = EvaluateXPathDotnetBridge,
        ["getPageSourceDotnetBridge"] = GetPageSourceDotnetBridge,
    };

    private DotNetTreeProvider Provider =>
        _provider ?? throw new InvalidOperationException(".NET bridge provider not created for this session.");

    private object? EnableDotnetBridge(ISessionContext ctx, JsonElement? parameters)
    {
        int? pid = null;
        if (parameters?.TryGetProperty("pid", out var pidEl) == true && pidEl.ValueKind == JsonValueKind.Number)
            pid = pidEl.GetInt32();

        int targetPid = pid ?? ctx.LastStartedProcessId;
        if (targetPid == 0)
            throw new InvalidOperationException(
                "No process PID available. Use appTopLevelWindow with dotnetBridge:true.");

        Provider.Connect(targetPid);
        return null;
    }

    private object? InjectDotnetBridge(ISessionContext ctx, JsonElement? parameters)
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
                    "Attach to a window first using the appTopLevelWindow capability.");
        }

        string x64Dll = Path.Combine(AppContext.BaseDirectory, "appium-dotnet-bridge.dll");
        if (!File.Exists(x64Dll))
            throw new FileNotFoundException($"Bridge DLL not found at: {x64Dll}");

        string x86Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "win-x86"));
        string x86Dll = Path.Combine(x86Root, "appium-dotnet-bridge.dll");
        string x86Stub = Path.Combine(x86Root, "BridgeInjectorX86Stub.exe");

        BridgeInjector.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
            throw new InvalidOperationException($"Could not resolve PID from window handle 0x{hwnd:X}.");

        try
        {
            BridgeInjector.InjectFromPidAutoBitness((int)pid, x64Dll, x86Dll, x86Stub);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(BuildDiagnosticMessage(ex, hwnd,
                "Injection failed at the Win32 API level (LoadLibrary/CreateRemoteThread). " +
                "Common causes: antivirus/EDR blocking cross-process code injection, or insufficient privileges — try running as Administrator."), ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(BuildDiagnosticMessage(ex, hwnd,
                "Access denied opening the target process. Try running as Administrator, " +
                "or check that antivirus/EDR is not blocking process access."), ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(BuildDiagnosticMessage(ex, hwnd,
                "I/O error reading or writing the bridge DLL or the x86 stub. " +
                "Verify the files are not locked by antivirus and were not corrupted during install."), ex);
        }
        catch (BadImageFormatException ex)
        {
            throw new InvalidOperationException(BuildDiagnosticMessage(ex, hwnd,
                "Bridge DLL bitness does not match the target process (32-bit vs 64-bit)."), ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(BuildDiagnosticMessage(ex, hwnd), ex);
        }

        Provider.Connect((int)pid);
        return null;
    }

    private object? FindElementDotnetBridge(ISessionContext ctx, JsonElement? parameters)
    {
        var (cond, contextId) = ParseFind(parameters);
        return Provider.FindFirstInWindow(cond, contextId);
    }

    private object? FindElementsDotnetBridge(ISessionContext ctx, JsonElement? parameters)
    {
        var (cond, contextId) = ParseFind(parameters);
        return Provider.FindAllInWindow(cond, contextId);
    }

    private object? EvaluateXPathDotnetBridge(ISessionContext ctx, JsonElement? parameters)
    {
        var p = parameters ?? throw new ArgumentException("Parameters required.");
        var expression = p.GetProperty("expression").GetString()
            ?? throw new ArgumentException("expression is required.");
        var multiple = p.TryGetProperty("multiple", out var m) && m.ValueKind == JsonValueKind.True;
        string? contextId = ReadContextId(p);
        return Provider.EvaluateXPathInWindow(expression, multiple, contextId);
    }

    private object? GetPageSourceDotnetBridge(ISessionContext ctx, JsonElement? parameters)
    {
        string? contextId = parameters is { } p ? ReadContextId(p) : null;
        return Provider.PageSourceInWindow(contextId);
    }

    private static (ConditionDto condition, string? contextElementId) ParseFind(JsonElement? parameters)
    {
        var p = parameters ?? throw new ArgumentException("Parameters required.");
        var conditionDto = JsonSerializer.Deserialize<ConditionDto>(p.GetProperty("condition").GetRawText())
            ?? throw new ArgumentException("condition is required.");
        return (conditionDto, ReadContextId(p));
    }

    private static string? ReadContextId(JsonElement p)
        => p.TryGetProperty("contextElementId", out var ctxProp) && ctxProp.ValueKind == JsonValueKind.String
            ? ctxProp.GetString()
            : null;

    private static string BuildDiagnosticMessage(Exception ex, IntPtr hwnd, string? hint = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(ex.Message);
        if (hint != null)
        {
            sb.AppendLine();
            sb.AppendLine(hint);
        }
        sb.AppendLine();
        sb.AppendLine("=== attachDotnetBridge diagnostics ===");

        BridgeInjector.GetWindowThreadProcessId(hwnd, out uint pid);
        sb.AppendLine($"  hwnd:        0x{hwnd:X}");
        sb.AppendLine($"  pid:         {pid}");

        if (pid != 0)
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                sb.AppendLine($"  process:     {process.ProcessName}.exe");
                sb.AppendLine($"  exe:         {process.MainModule?.FileName ?? "(unavailable)"}");

                var framework = BridgeInjector.DetectClrFramework((int)pid);
                sb.AppendLine($"  clr:         {framework}");

                try
                {
                    bool is32Bit = BridgeInjector.DetectIs32Bit((int)pid);
                    sb.AppendLine($"  bitness:     {(is32Bit ? "32-bit (WOW64)" : "64-bit")}");
                }
                catch (Exception bitnessEx) { sb.AppendLine($"  bitness:     (could not determine: {bitnessEx.Message})"); }

                string? clrModulePath = null;
                try
                {
                    foreach (ProcessModule m in process.Modules)
                    {
                        if (m.ModuleName.Equals("clr.dll", StringComparison.OrdinalIgnoreCase) ||
                            m.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase))
                        {
                            clrModulePath = m.FileName;
                            break;
                        }
                    }
                }
                catch { clrModulePath = "(module enumeration denied — try running as Administrator)"; }

                sb.AppendLine($"  clr module:  {clrModulePath ?? "(not loaded — is this a .NET process?)"}");
            }
            catch (Exception procEx)
            {
                sb.AppendLine($"  process:     (could not open: {procEx.Message})");
            }
        }

        sb.AppendLine("===================================");
        return sb.ToString();
    }
}

/// <summary>
/// <see cref="ITreeProvider"/> over <see cref="BridgeAgentService"/>. Opt-in only:
/// standard find / page source stay on real UIA even on a bridge-attached window;
/// the reflected tree is reached through the plugin's <c>*ViaDotnetBridge</c>
/// commands, which call the <c>*InWindow</c> helpers here.
/// </summary>
internal sealed class DotNetTreeProvider : ITreeProvider
{
    private static readonly string[] Prefixes = { "dotnet:", "dotnetcore:" };

    private readonly ISessionContext _ctx;
    private readonly BridgeAgentService _svc = new();
    private bool _attached;

    public DotNetTreeProvider(ISessionContext ctx) => _ctx = ctx;

    public void Connect(int pid)
    {
        _svc.Perf = _ctx.PerfEnabled ? _ctx.Perf : null;
        _svc.Connect(pid);
        _attached = true;
        _ctx.LogInfo("[dotnet-bridge] connected to CLR pid " + pid);
    }

    public string Name => "dotnet";
    public IReadOnlyList<string> ElementIdPrefixes => Prefixes;
    public bool OwnsElementId(string elementId) => BridgeAgentElement.IsDotnetId(elementId);
    public bool IsAttached => _attached;

    // The bridge tree is never auto-merged into the real UIA tree.
    public bool AutoRouteStandardFind => false;
    public bool AutoSwapsPageSource => false;
    public bool OwnsWindow(IntPtr hwnd, string windowTitle) => false;
    public string? GetWindowRootId(IntPtr hwnd, string windowTitle)
        => _svc.GetWindowRoot(hwnd, windowTitle)?.Id;

    // ── element-id-scoped ops (host routes here by the dotnet:/dotnetcore: prefix) ──

    public string? FindFirst(string rootElementId, ConditionDto condition, string scope)
        => _svc.FindFirst(_svc.GetById(rootElementId), condition, scope);

    public IReadOnlyList<string> FindAll(string rootElementId, ConditionDto condition, string scope)
        => _svc.FindAll(_svc.GetById(rootElementId), condition, scope);

    public object? EvaluateXPath(string rootElementId, string expression, bool multiple)
        => _svc.EvaluateXPath(_svc.GetById(rootElementId), expression, multiple);

    public object? GetProperty(string elementId, string propertyName)
    {
        var el = _svc.GetById(elementId);
        _svc.GetFreshInfo(el);
        return _svc.GetProperty(el, propertyName);
    }

    public string GetText(string elementId) => _svc.GetText(_svc.GetById(elementId));
    public string GetTagName(string elementId) => _svc.GetTagName(_svc.GetById(elementId));
    public object GetRect(string elementId) => _svc.GetRect(_svc.GetById(elementId));
    public string GetToggleState(string elementId) => _svc.GetToggleState(_svc.GetById(elementId));

    public bool IsSelected(string elementId)
    {
        var el = _svc.GetById(elementId);
        _svc.GetFreshInfo(el);
        return _svc.GetProperty(el, "IsSelected") is bool b && b;
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

    // ── window-scoped ops backing the *ViaDotnetBridge commands ──────────────────

    public string? FindFirstInWindow(ConditionDto condition, string? contextElementId)
        => _svc.FindFirst(ResolveRoot(contextElementId), condition, "subtree");

    public IReadOnlyList<string> FindAllInWindow(ConditionDto condition, string? contextElementId)
        => _svc.FindAll(ResolveRoot(contextElementId), condition, "subtree");

    public object? EvaluateXPathInWindow(string expression, bool multiple, string? contextElementId)
        => _svc.EvaluateXPath(ResolveRoot(contextElementId), expression, multiple);

    public string PageSourceInWindow(string? contextElementId)
    {
        var doc = new XmlDocument();
        _svc.BuildXml(ResolveRoot(contextElementId), doc, null);
        return doc.OuterXml;
    }

    private BridgeAgentElement ResolveRoot(string? contextElementId)
    {
        if (!_attached)
            throw new InvalidOperationException(
                "The .NET bridge is not attached to this session. Call 'windows: attachDotnetBridge' first.");

        if (contextElementId != null)
        {
            if (!BridgeAgentElement.IsDotnetId(contextElementId))
                throw new ArgumentException(
                    "contextElementId must be a .NET bridge element id (returned by a *ViaDotnetBridge command) " +
                    "— the bridge tree isn't correlated to the real UIA tree, so a plain UIA element id can't be used as a bridge search root.");
            return _svc.GetById(contextElementId);
        }

        var hwnd = _ctx.GetLiveRootHandle();
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("No active window for this session.");
        return _svc.GetWindowRoot(hwnd, _ctx.GetLiveRootName())
            ?? throw new InvalidOperationException("Could not resolve the .NET bridge's window root for the current window.");
    }
}
