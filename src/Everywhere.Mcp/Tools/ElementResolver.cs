using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using ModelContextProtocol.Protocol;

namespace Everywhere.Mcp.Tools;

internal static class ElementResolver
{
    public static (CallToolResult? Error, IVisualElement? Element) Resolve(SessionStore sessions, string elementIndex)
    {
        if (!int.TryParse(elementIndex, out var idx))
        {
            return (ToolErrors.Error($"Invalid element_index '{elementIndex}'. Expected integer."), null);
        }

        var hit = sessions.ResolveAcrossSessions(idx);
        if (hit is null)
        {
            return (ToolErrors.ElementIndexExpired(idx), null);
        }

        return (null, hit.Value.Element);
    }
}
