using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using DesktopDriverServer.Commands;

namespace DesktopDriverServer.Sap;

/// <summary>
/// In-process COM client for SAP GUI for Windows, via the SAP GUI Scripting API.
///
/// Standard UI Automation cannot see inside SAP GUI's custom-drawn controls — to it a
/// SAP session window is one opaque pane. SAP ships its own automation surface instead:
/// the <c>GuiApplication</c> COM object, reachable through the Running Object Table under
/// the moniker <c>"SAPGUI"</c>. This class is the counterpart of the Java / .NET bridges
/// (<see cref="DesktopDriverServer.DotNet.BridgeAgentService"/>) — an alternate tree
/// provider for a runtime real UIA can't reach — except no injection is needed: the
/// scripting engine already runs inside <c>saplogon.exe</c> whenever the user has ticked
/// "Enable scripting" in SAP GUI options (and the server allows it via profile parameter
/// <c>sapgui/user_scripting</c>).
///
/// All member access goes through <see cref="Disp"/> (hand-rolled <c>IDispatch</c>): SAP
/// GUI 8.00 registers only a 32-bit scripting typelib, so a 64-bit process cannot use
/// <c>dynamic</c> / <c>Type.InvokeMember</c> here. Every read is wrapped — SAP throws for
/// properties a given component type does not implement, and one unreadable node must not
/// abort a tree dump.
///
/// Threading: SAP GUI Scripting is single-threaded-apartment. The command dispatcher
/// already runs every handler inline on the process's one STA thread
/// (<see cref="Program"/>), so calls originating from <see cref="SapCommands"/> satisfy
/// that for free — do not call this class from any other thread.
///
/// Attach uses <see cref="Marshal.BindToMoniker"/>, the .NET equivalent of VBScript's
/// <c>GetObject("SAPGUI")</c>. <see cref="Marshal.GetActiveObject"/> is wrong here — it
/// resolves a ProgID via CLSID and "SAPGUI" is a moniker — and is absent from modern .NET.
/// </summary>
internal sealed class SapGuiClient : IDisposable
{
    internal const string IdPrefix = "sap:";
    private const string Moniker = "SAPGUI";
    private const int MaxDepth = 200;
    private const string NodeKeyAttr = "__sapNodeKey";

    private Disp? _engine;
    private Disp? _session;

    public bool IsAttached => _session != null;

    // ── Attach ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Binds to the running SAP GUI scripting engine and selects a session to drive.
    /// </summary>
    /// <param name="connectionIndex">Which open SAP connection (system) to use, default 0.</param>
    /// <param name="sessionIndex">Which session within that connection, default 0.</param>
    public object Attach(int connectionIndex = 0, int sessionIndex = 0)
    {
        // BindToMoniker + GetScriptingEngine trigger SAP's "a script is attaching" modal
        // when the client notification option is left on; the watchdog clicks it away so
        // an unattended attach doesn't hang. On a correctly configured machine the modal
        // never appears and the watchdog is a no-op.
        var engine = SapDialogWatchdog.Guard(() =>
        {
            object comObject;
            try
            {
                comObject = Marshal.BindToMoniker(Moniker);
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException(
                    "Could not bind to the SAP GUI scripting engine. Is SAP Logon (saplogon.exe) running? " +
                    $"(0x{ex.HResult:X8}: {ex.Message})", ex);
            }

            var sapgui = new Disp(comObject);
            try
            {
                return sapgui.GetObj("GetScriptingEngine");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "SAP GUI is running but scripting could not be started. Enable it in SAP GUI Options → " +
                    "Accessibility & Scripting → Scripting (client side), and ensure the server profile " +
                    $"parameter sapgui/user_scripting is TRUE. Underlying error: {ex.Message}", ex);
            }
        });

        _engine = engine;

        var connections = engine.GetObj("Children");
        int connectionCount = connections.GetInt("Count");
        if (connectionCount == 0)
        {
            _session = null;
            return new
            {
                attached = false,
                reason = "no_open_connection",
                message = "The SAP GUI scripting engine is reachable but no SAP connection is open. " +
                          "Log into an SAP system first, then attach again.",
                connectionCount = 0,
            };
        }

        if (connectionIndex < 0 || connectionIndex >= connectionCount)
            throw new ArgumentOutOfRangeException(nameof(connectionIndex),
                $"connectionIndex {connectionIndex} out of range (0..{connectionCount - 1}).");

        var connection = connections.GetObj("ElementAt", connectionIndex);
        var sessions = connection.GetObj("Children");
        int sessionCount = sessions.GetInt("Count");
        if (sessionIndex < 0 || sessionIndex >= sessionCount)
            throw new ArgumentOutOfRangeException(nameof(sessionIndex),
                $"sessionIndex {sessionIndex} out of range (0..{sessionCount - 1}).");

        _session = sessions.GetObj("ElementAt", sessionIndex);

        return new
        {
            attached = true,
            connectionCount,
            sessionCount,
            connectionIndex,
            sessionIndex,
            system = connection.GetString("Description"),
            sessionInfo = DescribeSessionInfo(_session),
        };
    }

    private static object? DescribeSessionInfo(Disp session)
    {
        try
        {
            var info = session.GetObj("Info");
            return new
            {
                systemName = info.GetString("SystemName"),
                client = info.GetString("Client"),
                user = info.GetString("User"),
                language = info.GetString("Language"),
                transaction = info.GetString("Transaction"),
                program = info.GetString("Program"),
                screenNumber = info.GetString("ScreenNumber"),
            };
        }
        catch { return null; }
    }

    // ── Element lookup ─────────────────────────────────────────────────────────

    private Disp SessionOrThrow() =>
        _session ?? throw new InvalidOperationException(
            "SAP GUI is not attached. Call sap.attach first (needs an open SAP session).");

    internal static string RawId(string elementId) =>
        elementId.StartsWith(IdPrefix, StringComparison.Ordinal) ? elementId[IdPrefix.Length..] : elementId;

    internal static bool IsSapId(string? id) =>
        id != null && id.StartsWith(IdPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Resolves an element id to a live SAP component via <c>session.FindById(id, false)</c>
    /// (<c>false</c> = return null instead of throwing). SAP ids
    /// (e.g. <c>/app/con[0]/ses[0]/wnd[0]/usr/txtRSYST-BNAME</c>) are stable and globally
    /// unique, so we always re-resolve rather than trust a cached COM pointer.
    /// </summary>
    public Disp Resolve(string elementId)
    {
        var raw = RawId(elementId);
        var comp = SessionOrThrow().CallObjOrNull("FindById", raw, false);
        if (comp == null)
            throw new KeyNotFoundException($"No SAP element with id '{raw}' on the current screen.");
        return comp;
    }

    public string? FindFirstById(string sapId)
    {
        var comp = SessionOrThrow().CallObjOrNull("FindById", sapId, false);
        return comp == null ? null : IdPrefix + comp.GetString("Id");
    }

    // ── Page source / tree ─────────────────────────────────────────────────────

    public string GetPageSourceXml(string? contextElementId)
    {
        var doc = new XmlDocument();
        var root = ResolveContextOrActiveWindow(contextElementId);
        var rootXml = BuildXml(doc, root, 0, null, null) ?? doc.CreateElement("DummyRoot");
        doc.AppendChild(rootXml);
        return doc.OuterXml;
    }

    public object DumpTree(string? contextElementId) => GetPageSourceXml(contextElementId);

    private Disp ResolveContextOrActiveWindow(string? contextElementId)
    {
        if (!string.IsNullOrEmpty(contextElementId))
            return Resolve(contextElementId!);
        var session = SessionOrThrow();
        return session.GetObjOrNull("ActiveWindow")
            ?? session.GetObj("Children").GetObj("ElementAt", 0);
    }

    /// <summary>
    /// Evaluates a full XPath 1.0 expression against a materialised snapshot of the SAP
    /// subtree, reusing <see cref="XPathEvaluator"/> (System.Xml.XPath) exactly as the UIA
    /// and .NET-bridge paths do. Result nodes carry the SAP element id.
    /// </summary>
    public object? EvaluateXPath(string? contextElementId, string expression, bool multiple)
    {
        var doc = new XmlDocument();
        var nodeToId = new Dictionary<string, string>(StringComparer.Ordinal);
        int counter = 0;
        var root = ResolveContextOrActiveWindow(contextElementId);
        var rootXml = BuildXml(doc, root, 0, nodeToId, () => "x" + counter++)
                      ?? doc.CreateElement("DummyRoot");
        doc.AppendChild(rootXml);

        var ids = XPathEvaluator.Evaluate(
            doc, null, NodeKeyAttr, expression, multiple,
            nodeKey => nodeToId.TryGetValue(nodeKey, out var elId) ? elId : null);

        return multiple ? ids.ToArray() : (ids.Count > 0 ? (object)ids[0] : null);
    }

    private XmlElement? BuildXml(
        XmlDocument doc,
        Disp component,
        int depth,
        Dictionary<string, string>? nodeToId,
        Func<string>? nextNodeKey)
    {
        if (depth > MaxDepth) return null;

        XmlElement el;
        string type;
        try
        {
            var sapId = component.GetString("Id");
            type = component.GetString("Type");
            el = doc.CreateElement(ValidTag(type));

            if (nodeToId != null && nextNodeKey != null)
            {
                var key = nextNodeKey();
                el.SetAttribute(NodeKeyAttr, key);
                nodeToId[key] = IdPrefix + sapId;
            }

            el.SetAttribute("Id", sapId);
            el.SetAttribute("Type", type);
            el.SetAttribute("Name", component.GetString("Name"));
            var text = Sanitize(component.GetString("Text"));
            el.SetAttribute("Text", text);
            el.SetAttribute("Tooltip", Sanitize(component.GetString("Tooltip")));
            el.SetAttribute("Changeable", component.GetBool("Changeable").ToString());
            el.SetAttribute("IconName", component.GetString("IconName"));

            // GuiVComponent screen geometry — absolute screen pixels. SAP has no cheap
            // "root rect" to offset against; the Actions layer can use absolute coords
            // directly, so emit as-is.
            el.SetAttribute("x", component.GetInt("ScreenLeft").ToString());
            el.SetAttribute("y", component.GetInt("ScreenTop").ToString());
            el.SetAttribute("width", component.GetInt("Width").ToString());
            el.SetAttribute("height", component.GetInt("Height").ToString());

            if (text.Length > 0) el.AppendChild(doc.CreateTextNode(text));
        }
        catch
        {
            return null;
        }

        // Children — only containers expose a Children collection.
        try
        {
            if (component.GetBool("ContainerType"))
            {
                var children = component.GetObjOrNull("Children");
                int n = children?.GetInt("Count") ?? 0;
                for (int i = 0; i < n; i++)
                {
                    Disp? child;
                    try { child = children!.GetObjOrNull("ElementAt", i); }
                    catch { continue; }
                    if (child == null) continue;
                    var childXml = BuildXml(doc, child, depth + 1, nodeToId, nextNodeKey);
                    if (childXml != null) el.AppendChild(childXml);
                }
            }
        }
        catch { }

        // Virtualised controls whose rows/nodes are not children.
        try { AppendGridRows(doc, el, component, type); } catch { }
        try { AppendTreeNodes(doc, el, component, type); } catch { }

        return el;
    }

    /// <summary>
    /// <c>GuiGridView</c> (ALV grid) does not expose cells through <c>Children</c> — rows
    /// are virtualised and addressed by index + column id. Emit the visible rows as
    /// <c>GridRow</c> / <c>GridCell</c> children so they show up in page source and are
    /// XPath-addressable. Full row access still needs scrolling via the Actions layer.
    /// </summary>
    private static void AppendGridRows(XmlDocument doc, XmlElement parent, Disp component, string type)
    {
        if (type != "GuiGridView") return;

        int rowCount = component.GetInt("RowCount");
        int firstVisible = component.GetInt("FirstVisibleRow");
        int visibleRows = component.GetInt("VisibleRowCount");
        if (visibleRows <= 0) visibleRows = rowCount;
        var columnOrder = component.GetObjOrNull("ColumnOrder");
        int colCount = columnOrder?.GetInt("Count") ?? 0;

        int lastRow = Math.Min(rowCount, firstVisible + visibleRows);
        for (int r = firstVisible; r < lastRow; r++)
        {
            var rowEl = doc.CreateElement("GridRow");
            rowEl.SetAttribute("Index", r.ToString());
            for (int c = 0; c < colCount; c++)
            {
                string colId;
                try { colId = columnOrder!.Get("ElementAt", c)?.ToString() ?? ""; }
                catch { continue; }
                if (colId.Length == 0) continue;
                var cellEl = doc.CreateElement("GridCell");
                cellEl.SetAttribute("Column", colId);
                cellEl.SetAttribute("Text", Sanitize(component.GetString("GetCellValue", r, colId)));
                rowEl.AppendChild(cellEl);
            }
            parent.AppendChild(rowEl);
        }
        parent.SetAttribute("RowCount", rowCount.ToString());
    }

    /// <summary>
    /// <c>GuiTree</c> nodes are addressed by key, not exposed as <c>Children</c>. Emit the
    /// currently known nodes as <c>TreeNode</c> children.
    /// </summary>
    private static void AppendTreeNodes(XmlDocument doc, XmlElement parent, Disp component, string type)
    {
        if (type != "GuiTree") return;

        var keys = component.CallObjOrNull("GetAllNodeKeys");
        if (keys == null) return;
        int n = keys.GetInt("Count");
        for (int i = 0; i < n; i++)
        {
            string key;
            try { key = keys.Get("ElementAt", i)?.ToString() ?? ""; }
            catch { continue; }
            if (key.Length == 0) continue;
            var nodeEl = doc.CreateElement("TreeNode");
            nodeEl.SetAttribute("Key", key);
            nodeEl.SetAttribute("Text", Sanitize(component.GetString("GetNodeTextByKey", key)));
            nodeEl.SetAttribute("IsExpanded", component.GetBool("IsFolderExpanded", key).ToString());
            parent.AppendChild(nodeEl);
        }
    }

    // ── Property access ────────────────────────────────────────────────────────

    public object? GetProperty(string elementId, string property)
    {
        var comp = Resolve(elementId);
        return property.ToLowerInvariant() switch
        {
            "id" => comp.GetString("Id"),
            "type" => comp.GetString("Type"),
            "name" => comp.GetString("Name"),
            "text" or "value" => comp.GetString("Text"),
            "tooltip" => comp.GetString("Tooltip"),
            "changeable" or "enabled" => comp.GetBool("Changeable"),
            "iconname" => comp.GetString("IconName"),
            "screenleft" or "x" => comp.GetInt("ScreenLeft").ToString(),
            "screentop" or "y" => comp.GetInt("ScreenTop").ToString(),
            "width" => comp.GetInt("Width").ToString(),
            "height" => comp.GetInt("Height").ToString(),
            _ => comp.GetString(property),
        };
    }

    public string GetText(string elementId) => Resolve(elementId).GetString("Text");

    public string GetTagName(string elementId) => Resolve(elementId).GetString("Type");

    public object GetRect(string elementId)
    {
        var comp = Resolve(elementId);
        return new
        {
            x = comp.GetInt("ScreenLeft"),
            y = comp.GetInt("ScreenTop"),
            width = comp.GetInt("Width"),
            height = comp.GetInt("Height"),
        };
    }

    // ── Interaction ────────────────────────────────────────────────────────────

    public void SetValue(string elementId, string value)
    {
        var comp = Resolve(elementId);
        switch (comp.GetString("Type"))
        {
            case "GuiCheckBox":
                comp.Set("Selected", ParseBool(value));
                break;
            case "GuiRadioButton":
                if (ParseBool(value)) comp.Call("Select");
                break;
            case "GuiComboBox":
                comp.Set("Key", value);
                break;
            default:
                comp.Set("Text", value); // GuiTextField, GuiCTextField, GuiPasswordField, GuiTextEdit…
                break;
        }
    }

    public void Invoke(string elementId)
    {
        var comp = Resolve(elementId);
        switch (comp.GetString("Type"))
        {
            case "GuiButton":
                comp.Call("Press");
                break;
            case "GuiTab":
            case "GuiRadioButton":
            case "GuiMenu":
                comp.Call("Select");
                break;
            case "GuiCheckBox":
                comp.Set("Selected", !comp.GetBool("Selected"));
                break;
            default:
                try { comp.Call("Press"); }
                catch { comp.Call("Select"); }
                break;
        }
    }

    public void SetFocus(string elementId) => Resolve(elementId).Call("SetFocus");

    public void Select(string elementId) => Resolve(elementId).Call("Select");

    /// <summary>Sends a virtual key to a window, e.g. 0 = Enter, 8 = F8.</summary>
    public void SendVKey(int vkey, string? windowElementId = null)
    {
        var wnd = windowElementId != null ? Resolve(windowElementId) : SessionOrThrow().GetObj("ActiveWindow");
        wnd.Call("SendVKey", vkey);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// SAP's <c>Type</c> string (e.g. "GuiTextField", "GuiButton", "GuiGridView") is already
    /// a stable, language-neutral PascalCase identifier — use it directly as the XML tag,
    /// falling back to "GuiComponent" when it is somehow not a valid XML name.
    /// </summary>
    private static string ValidTag(string type)
    {
        if (string.IsNullOrEmpty(type)) return "GuiComponent";
        try { XmlConvert.VerifyName(type); return type; }
        catch { return "GuiComponent"; }
    }

    private static bool ParseBool(string s) =>
        s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" ||
        s.Equals("x", StringComparison.OrdinalIgnoreCase) || s.Equals("yes", StringComparison.OrdinalIgnoreCase);

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch == '\t' || ch == '\n' || ch == '\r' ||
                (ch >= 0x20 && ch <= 0xD7FF) || (ch >= 0xE000 && ch <= 0xFFFD))
                sb.Append(ch);
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        _session = null;
        _engine = null;
    }
}
