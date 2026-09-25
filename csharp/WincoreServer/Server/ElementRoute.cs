using System.Text.Json;
using Wincore.ServerSdk;
using WincoreServer.State;
using WincoreServer.Uia3;

namespace WincoreServer.Server;

/// <summary>UIA side of an element-scoped command: receives the already-resolved element.</summary>
public delegate object? UiaElementHandler(SessionState state, IUIAutomationElement element, JsonElement parameters);

/// <summary>Tree-provider side of an element-scoped command.</summary>
public delegate object? ProviderElementHandler(ITreeProvider provider, string elementId, JsonElement parameters);

/// <summary>
/// Single routing point for every command that acts on an existing <c>elementId</c>.
/// The id is resolved once: an id minted by a tree provider (Java agent, .NET bridge,
/// SAP, …) goes to the provider handler, anything else is looked up in the UIA element
/// table and handed to the UIA handler. Handlers never check ids themselves.
///
/// A null provider handler marks the command UIA-only (window operations, session
/// root); a provider id sent to it fails with a uniform NotSupportedException.
/// </summary>
public static class ElementRoute
{
    public static Func<SessionState, JsonElement?, object?> Create(
        string method, UiaElementHandler uia, ProviderElementHandler? provider)
    {
        return (state, parameters) =>
        {
            var p = parameters ?? throw new ArgumentException("Parameters required.");
            var elementId = ReadElementId(p);

            if (state.Providers.TryResolve(elementId, out var treeProvider))
            {
                if (provider == null)
                {
                    throw new NotSupportedException(
                        $"'{method}' is only supported for UI Automation elements, not for elements " +
                        $"of the '{treeProvider.Name}' tree provider.");
                }
                return provider(treeProvider, elementId, p);
            }

            return uia(state, state.GetElement(elementId), p);
        };
    }

    private static string ReadElementId(JsonElement parameters)
    {
        // TryGetProperty, not GetProperty: a missing key would otherwise surface as a
        // KeyNotFoundException, which the server reports as ElementNotFound.
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("elementId", out var idProp)
            || idProp.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(idProp.GetString()))
        {
            throw new ArgumentException("elementId is required.");
        }
        return idProp.GetString()!;
    }
}
