using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Wincore.ServerSdk;

namespace DesktopDriverServer.Plugins;

/// <summary>
/// Discovers external <see cref="IServerPlugin"/> assemblies. Each entry on the
/// <c>DESKTOP_DRIVER_PLUGINS</c> environment variable (<c>;</c>-separated absolute
/// directories, set by an installed <c>appium-wincore-*</c> Appium plugin) is a
/// folder holding a <c>plugin.json</c> manifest and its .NET assembly.
///
/// <code>
/// plugin.json:  { "entry": "WincoreDotnetBridge.dll", "type": "Wincore.DotNetBridge.Plugin", "sdkVersion": "1.0.0" }
/// </code>
///
/// Each plugin is loaded into its own <see cref="AssemblyLoadContext"/> so it can
/// carry private dependencies; the shared <c>WincoreServerSdk</c> contract assembly
/// is resolved back to the host's already-loaded copy (never a plugin-local one) so
/// the interface identity matches. A plugin whose declared <c>sdkVersion</c> has a
/// different major than the host's is refused.
/// </summary>
public static class PluginLoader
{
    private sealed record Manifest(string Entry, string Type, string SdkVersion);

    public static IEnumerable<IServerPlugin> LoadExternal(Action<string> log)
    {
        var raw = Environment.GetEnvironmentVariable("DESKTOP_DRIVER_PLUGINS");
        if (string.IsNullOrWhiteSpace(raw)) yield break;

        var hostMajor = SdkContract.MajorOf(SdkContract.Version);

        foreach (var dir in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            IServerPlugin? plugin = null;
            try
            {
                plugin = LoadOne(dir, hostMajor, log);
            }
            catch (Exception ex)
            {
                log($"[plugins] failed to load plugin from '{dir}': {ex.Message}");
            }
            if (plugin != null) yield return plugin;
        }
    }

    private static IServerPlugin? LoadOne(string dir, int hostMajor, Action<string> log)
    {
        var manifestPath = Path.Combine(dir, "plugin.json");
        if (!File.Exists(manifestPath))
        {
            log($"[plugins] no plugin.json in '{dir}' — skipped");
            return null;
        }

        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("plugin.json did not deserialize");

        if (SdkContract.MajorOf(manifest.SdkVersion) != hostMajor)
        {
            log($"[plugins] '{dir}': sdkVersion {manifest.SdkVersion} is incompatible with host SDK {SdkContract.Version} — skipped");
            return null;
        }

        var asmPath = Path.Combine(dir, manifest.Entry);
        var ctx = new PluginLoadContext(asmPath);
        var asm = ctx.LoadFromAssemblyPath(asmPath);

        var type = asm.GetType(manifest.Type)
            ?? throw new InvalidOperationException($"type '{manifest.Type}' not found in {manifest.Entry}");
        var plugin = (IServerPlugin?)Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"could not instantiate '{manifest.Type}'");

        if (SdkContract.MajorOf(plugin.SdkVersion) != hostMajor)
        {
            log($"[plugins] '{plugin.Name}': runtime SdkVersion {plugin.SdkVersion} incompatible with host {SdkContract.Version} — skipped");
            return null;
        }

        return plugin;
    }

    /// <summary>
    /// Load context that resolves a plugin's private dependencies from its own
    /// folder but defers the shared contract assembly (and anything already loaded
    /// by the host) to the default context, so <c>ITreeProvider</c> etc. are the
    /// same <see cref="Type"/> on both sides.
    /// </summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginPath) : base(isCollectible: false)
            => _resolver = new AssemblyDependencyResolver(pluginPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Shared contract + framework assemblies come from the host.
            if (assemblyName.Name == "WincoreServerSdk")
                return null;

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path != null ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }
    }
}
