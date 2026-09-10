using System.Text.Json;

namespace Wincore.ServerSdk;

/// <summary>
/// A JSON-RPC command handler contributed by a plugin. Same shape as the host's own
/// handlers: given the session context and the request's <c>params</c> element,
/// return a JSON-serializable result (or null).
/// </summary>
public delegate object? PluginCommandHandler(ISessionContext context, JsonElement? parameters);

/// <summary>
/// Entry point of a WincoreServer plugin. Discovered by <c>PluginLoader</c>
/// from a folder on <c>WINCORE_SERVER_PLUGINS</c>; the loader instantiates the type
/// named in the folder's <c>plugin.json</c> and calls this once at server start.
/// </summary>
public interface IServerPlugin
{
    /// <summary>Stable short name for logs and collision diagnostics, e.g. "dotnet-bridge".</summary>
    string Name { get; }

    /// <summary>
    /// Major version of the <c>WincoreServerSdk</c> contract this plugin was built
    /// against. The loader refuses the plugin when this differs from the host's SDK
    /// major version. Use <see cref="SdkContract.Version"/>.
    /// </summary>
    string SdkVersion { get; }

    /// <summary>
    /// Extra JSON-RPC methods this plugin answers (e.g. <c>windows: attachDotnetBridge</c>,
    /// <c>findElementViaDotnetBridge</c>). Merged into the host dispatcher; a name
    /// that collides with a core method or another plugin's method is a startup error.
    /// </summary>
    IReadOnlyDictionary<string, PluginCommandHandler> GetCommands();

    /// <summary>
    /// Creates this plugin's tree provider for a session, or null if the plugin adds
    /// only commands. Called once per session before the first command runs.
    /// </summary>
    ITreeProvider? CreateProvider(ISessionContext context);
}

/// <summary>Version constants for the plugin contract.</summary>
public static class SdkContract
{
    /// <summary>
    /// The contract version. Its <b>major</b> component is the compatibility key:
    /// the loader loads a plugin only when its declared <see cref="IServerPlugin.SdkVersion"/>
    /// has the same major.
    /// </summary>
    public const string Version = "1.0.0";

    public static int MajorOf(string version)
    {
        var dot = version.IndexOf('.');
        var head = dot < 0 ? version : version[..dot];
        return int.TryParse(head, out var m) ? m : -1;
    }
}
