using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;

namespace Everywhere.Mcp.Tools;

/// <summary>
/// Maps the user-supplied <c>app</c> string (bundle id / process name / window title fragment)
/// onto a live <see cref="IVisualElement"/> rooted at that app's top-level window.
/// </summary>
internal static class AppResolver
{
    public readonly record struct ResolvedApp(IVisualElement Window, string AppKey, int ProcessId);

    public static ResolvedApp? Resolve(IVisualElementContext context, string app)
    {
        if (string.IsNullOrWhiteSpace(app))
        {
            return null;
        }

        foreach (var screen in context.Screens)
        {
            foreach (var topLevel in screen.Children)
            {
                if (Matches(topLevel, app))
                {
                    var key = AppKey.FromProcessId(topLevel.ProcessId);
                    return new ResolvedApp(topLevel, key, topLevel.ProcessId);
                }
            }
        }

        var focused = context.FocusedElement;
        if (focused != null)
        {
            var top = WalkToTopLevel(focused);
            if (top != null && Matches(top, app))
            {
                var key = AppKey.FromProcessId(top.ProcessId);
                return new ResolvedApp(top, key, top.ProcessId);
            }
        }

        return null;
    }

    public static IReadOnlyList<ResolvedApp> ListApps(IVisualElementContext context)
    {
        var seen = new HashSet<int>();
        var result = new List<ResolvedApp>();
        foreach (var screen in context.Screens)
        {
            foreach (var topLevel in screen.Children)
            {
                if (topLevel.ProcessId <= 0 || !seen.Add(topLevel.ProcessId))
                {
                    continue;
                }

                var key = AppKey.FromProcessId(topLevel.ProcessId);
                result.Add(new ResolvedApp(topLevel, key, topLevel.ProcessId));
            }
        }

        return result;
    }

    private static IVisualElement? WalkToTopLevel(IVisualElement element)
    {
        var current = element;
        while (current != null && current.Type != VisualElementType.TopLevel)
        {
            current = current.Parent;
        }
        return current;
    }

    private static bool Matches(IVisualElement window, string query)
    {
        var key = AppKey.FromProcessId(window.ProcessId);
        if (AppKey.MatchesQuery(key, query))
        {
            return true;
        }

        var name = window.Name;
        return !string.IsNullOrEmpty(name)
               && name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
