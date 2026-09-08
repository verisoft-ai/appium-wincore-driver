using System.Runtime.InteropServices;
using System.Text;

namespace DesktopDriverServer.Sap;

/// <summary>
/// Dismisses SAP GUI's "A script is attempting to access SAP GUI" / "A script is opening a
/// connection" notification dialogs.
///
/// When the client-side options "Notify when a script attaches / opens a connection" are
/// left ticked, SAP GUI shows a modal <c>#32770</c> dialog the first time a script touches
/// it. That dialog blocks the calling (STA) thread inside the COM call until a human clicks
/// OK — fatal for unattended automation. SAP's own guidance is to untick those boxes, but
/// we cannot rely on every machine being configured, so this watchdog runs on a plain
/// background thread (Win32 only — no COM, no STA) for a few seconds around an attach and
/// clicks OK if the dialog appears.
///
/// This only auto-confirms the low-risk "attach" prompt on a machine where scripting is
/// already enabled; it does not change any setting and does not touch the higher-risk
/// per-action prompts beyond the initial connection notification.
/// </summary>
internal static class SapDialogWatchdog
{
    private const int GW_TIMEOUT_MS = 6000;
    private const int BM_CLICK = 0x00F5;
    private const uint WM_COMMAND = 0x0111;
    private const int IDOK = 1;

    /// <summary>
    /// Runs <paramref name="action"/> while a background thread watches for the SAP script
    /// notification dialog and clicks its OK button.
    /// </summary>
    public static T Guard<T>(Func<T> action)
    {
        using var stop = new CancellationTokenSource();
        var watcher = new Thread(() => Watch(stop.Token)) { IsBackground = true, Name = "sap-dialog-watchdog" };
        watcher.Start();
        try
        {
            return action();
        }
        finally
        {
            stop.Cancel();
            watcher.Join(500);
        }
    }

    private static void Watch(CancellationToken ct)
    {
        var deadline = Environment.TickCount + GW_TIMEOUT_MS;
        while (!ct.IsCancellationRequested && Environment.TickCount < deadline)
        {
            try
            {
                if (TryDismissOnce()) return; // dialog handled — done
            }
            catch { /* best effort */ }
            Thread.Sleep(150);
        }
    }

    private static bool TryDismissOnce()
    {
        var hits = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (GetClassString(hwnd) != "#32770") return true;      // standard dialog class
            if (!ProcessIsSapLogon(hwnd)) return true;
            if (!DialogMentionsScript(hwnd)) return true;
            hits.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        foreach (var dlg in hits)
        {
            var ok = FindChildButton(dlg, "OK") ;
            if (ok != IntPtr.Zero)
            {
                SendMessage(ok, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            }
            else
            {
                // Fall back to posting the default-command to the dialog itself.
                SendMessage(dlg, WM_COMMAND, (IntPtr)IDOK, IntPtr.Zero);
            }
            return true;
        }
        return false;
    }

    private static bool ProcessIsSapLogon(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName.Equals("saplogon", StringComparison.OrdinalIgnoreCase)
                || p.ProcessName.StartsWith("sapgui", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool DialogMentionsScript(IntPtr dlg)
    {
        bool found = false;
        EnumChildWindows(dlg, (child, _) =>
        {
            var text = GetWindowTextString(child);
            if (text.Contains("script", StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static IntPtr FindChildButton(IntPtr dlg, string caption)
    {
        IntPtr match = IntPtr.Zero;
        EnumChildWindows(dlg, (child, _) =>
        {
            if (GetClassString(child) != "Button") return true;
            var text = GetWindowTextString(child).Replace("&", "");
            if (text.Equals(caption, StringComparison.OrdinalIgnoreCase))
            {
                match = child;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return match;
    }

    // ── Win32 ─────────────────────────────────────────────────────────────────

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static string GetClassString(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetWindowTextString(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
