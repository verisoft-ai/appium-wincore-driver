namespace Wincore.ServerSdk;

/// <summary>
/// The narrowed view of the host's per-session state that a server plugin and its
/// <see cref="ITreeProvider"/> are given. Deliberately small: a provider owns its
/// own element cache and connection state (as the in-repo Java/.NET bridges always
/// have) — all it needs from the host is the current window and the perf/log sinks.
/// </summary>
public interface ISessionContext
{
    /// <summary>
    /// Native window handle of the session's current root (the attached top-level
    /// window), or <see cref="IntPtr.Zero"/> when the root is the desktop or an
    /// element with no HWND. Re-resolved live per call by the host.
    /// </summary>
    IntPtr GetLiveRootHandle();

    /// <summary>Name/title of the session's current root, or "" if unavailable.</summary>
    string GetLiveRootName();

    /// <summary>True when the <c>perfMetrics</c> capability is on for this session.</summary>
    bool PerfEnabled { get; }

    /// <summary>Perf sink; a <see cref="NullPerfSink"/> when <see cref="PerfEnabled"/> is false.</summary>
    IPerfSink Perf { get; }

    /// <summary>Writes a line to the host's stderr log stream (forwarded to the Appium driver log).</summary>
    void LogInfo(string message);

    /// <summary>Writes an error line to the host's stderr log stream.</summary>
    void LogError(string message);
}
