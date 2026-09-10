using System.Text.Json;
using DesktopDriverServer.State;
using Wincore.ServerSdk;

namespace DesktopDriverServer.Plugins;

/// <summary>
/// Owns the set of <see cref="IServerPlugin"/>s for the process — the in-tree
/// built-ins plus anything <see cref="PluginLoader"/> found on
/// <c>DESKTOP_DRIVER_PLUGINS</c>. Merges their command handlers for the dispatcher
/// and creates their per-session tree providers.
/// </summary>
public sealed class PluginHost
{
    private readonly List<IServerPlugin> _plugins;

    private PluginHost(List<IServerPlugin> plugins) => _plugins = plugins;

    public IReadOnlyList<IServerPlugin> Plugins => _plugins;

    /// <summary>
    /// Loads every external plugin listed on <c>DESKTOP_DRIVER_PLUGINS</c> (the
    /// Java and .NET bridges each ship as their own repo + Appium plugin). The
    /// core server has no built-in providers.
    /// </summary>
    public static PluginHost Create(Action<string> log)
    {
        var plugins = new List<IServerPlugin>(PluginLoader.LoadExternal(log));

        foreach (var p in plugins)
            log($"[plugins] loaded '{p.Name}' (sdk {p.SdkVersion})");

        return new PluginHost(plugins);
    }

    /// <summary>
    /// Every plugin-contributed JSON-RPC handler, adapted to the host handler shape.
    /// A name that collides with another plugin's is a hard error (the dispatcher
    /// also rejects collisions with core methods).
    /// </summary>
    public Dictionary<string, Func<SessionState, JsonElement?, object?>> GetPluginCommands()
    {
        var merged = new Dictionary<string, Func<SessionState, JsonElement?, object?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in _plugins)
        {
            foreach (var (method, handler) in plugin.GetCommands())
            {
                if (merged.ContainsKey(method))
                    throw new InvalidOperationException(
                        $"Plugin '{plugin.Name}' contributes command '{method}' which another plugin already provides.");
                merged[method] = (state, parameters) => handler(state, parameters);
            }
        }
        return merged;
    }

    /// <summary>Creates each plugin's provider for the session and registers it.</summary>
    public void CreateProviders(SessionState state)
    {
        foreach (var plugin in _plugins)
        {
            var provider = plugin.CreateProvider(state);
            if (provider != null)
                state.Providers.Register(provider);
        }
    }
}
