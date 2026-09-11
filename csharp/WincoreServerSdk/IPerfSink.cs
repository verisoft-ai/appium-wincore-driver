namespace Wincore.ServerSdk;

/// <summary>
/// Per-session performance sink, opt-in via the <c>perfMetrics</c> capability. A
/// tree provider records one entry per RPC round trip (e.g. <c>java.getChildren</c>)
/// so a benchmark can attribute where a slow high-level operation spends its time.
/// When perf metrics are disabled the host supplies a no-op sink.
/// </summary>
public interface IPerfSink
{
    void Record(string label, double elapsedMs);
}

/// <summary>No-op <see cref="IPerfSink"/> used when the perfMetrics capability is off.</summary>
public sealed class NullPerfSink : IPerfSink
{
    public static readonly NullPerfSink Instance = new();
    public void Record(string label, double elapsedMs) { }
}
