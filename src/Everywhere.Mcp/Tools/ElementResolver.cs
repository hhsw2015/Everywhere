using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using ModelContextProtocol.Protocol;

namespace Everywhere.Mcp.Tools;

internal static class ElementResolver
{
    /// <summary>
    /// Resolve <paramref name="elementIndex"/> into an <see cref="IVisualElement"/>. When
    /// <paramref name="appHint"/> is provided, the lookup is scoped to that app's session
    /// first — preventing a stray index from being silently mapped to the wrong app's
    /// element (e.g. <c>click(app="Safari", element_index="5")</c> hitting Slack).
    /// </summary>
    public static (CallToolResult? Error, IVisualElement? Element) Resolve(
        SessionStore sessions,
        string elementIndex,
        string? appHint = null)
    {
        if (!int.TryParse(elementIndex, out var idx))
        {
            return (ToolErrors.Error($"Invalid element_index '{elementIndex}'. Expected integer."), null);
        }

        if (!string.IsNullOrEmpty(appHint))
        {
            var resolved = AppKey.FromProcessId(0); // placeholder; app-key is just a string match
            // First try the exact session for the named app, if one exists with that key.
            // We don't know the actual appKey from the hint without an IVisualElementContext;
            // walk known sessions and pick the one whose key contains/equals the hint.
            foreach (var key in sessions.GetKeys())
            {
                if (!AppKey.MatchesQuery(key, appHint)) continue;
                var s = sessions.GetCurrent(key);
                if (s is null) continue;
                if (s.ElementsByIndex.TryGetValue(idx, out var element))
                {
                    return (null, element);
                }
            }
            // appHint set but no scoped match → tell the caller the index isn't in
            // the named app's snapshot rather than silently leaking from another app.
            return (ToolErrors.Error(
                $"Element index {idx} not found in current snapshot for app '{appHint}'. " +
                "Re-issue get_app_state for that app and use the indices it returns."), null);
        }

        var hit = sessions.ResolveAcrossSessions(idx);
        if (hit is null)
        {
            return (ToolErrors.ElementIndexExpired(idx), null);
        }

        return (null, hit.Value.Element);
    }
}
