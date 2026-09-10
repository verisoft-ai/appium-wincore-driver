using Wincore.ServerSdk;

namespace DesktopDriverServer.Plugins;

/// <summary>
/// Per-session set of <see cref="ITreeProvider"/>s contributed by loaded plugins.
/// Command handlers route through here instead of hard-coding
/// <c>JavaAgentElement.IsJavaId</c> / <c>BridgeAgentElement.IsDotnetId</c> checks.
/// </summary>
public sealed class ProviderRegistry : IDisposable
{
    private readonly List<ITreeProvider> _providers = new();

    public IReadOnlyList<ITreeProvider> All => _providers;

    public void Register(ITreeProvider provider) => _providers.Add(provider);

    /// <summary>
    /// The provider that minted <paramref name="elementId"/> (by id prefix), if any.
    /// </summary>
    public bool TryResolve(string? elementId, out ITreeProvider provider)
    {
        provider = null!;
        if (string.IsNullOrEmpty(elementId)) return false;
        foreach (var p in _providers)
        {
            if (p.OwnsElementId(elementId!))
            {
                provider = p;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// An attached provider that owns the given top-level window — for a fresh
    /// find / page source / xpath rooted at that window rather than an element id.
    /// </summary>
    public bool TryResolveWindow(IntPtr hwnd, string windowTitle, out ITreeProvider provider)
    {
        provider = null!;
        foreach (var p in _providers)
        {
            if (p.IsAttached && p.OwnsWindow(hwnd, windowTitle))
            {
                provider = p;
                return true;
            }
        }
        return false;
    }

    public void Dispose()
    {
        foreach (var p in _providers)
        {
            try { p.Dispose(); } catch { }
        }
        _providers.Clear();
    }
}
