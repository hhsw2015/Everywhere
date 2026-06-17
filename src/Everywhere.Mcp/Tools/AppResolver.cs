using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;

namespace Everywhere.Mcp.Tools;

/// <summary>
/// Maps a user-supplied <c>app</c> string (process name / window title fragment) onto a live
/// top-level <see cref="IVisualElement"/>. When several windows match (e.g. a menubar overlay
/// plus the real browser window of the same app), prefer the largest visible window so
/// "the browser" never resolves to a 25-pixel menubar widget.
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

        // Build a list of candidate query strings: original hint plus any category aliases
        // ("browser" → arc/safari/chrome/...) so a generic noun resolves to whichever
        // concrete app the user has open.
        var queries = new List<string> { app };
        queries.AddRange(AppAliases.Expand(app));

        var candidates = new List<(IVisualElement Window, string AppKey, int ProcessId, long Area)>();
        foreach (var screen in context.Screens)
        {
            foreach (var topLevel in screen.Children)
            {
                if (!queries.Any(q => Matches(topLevel, q)))
                {
                    continue;
                }
                var key = AppKey.FromProcessId(topLevel.ProcessId);
                var bounds = topLevel.BoundingRectangle;
                var area = (long)Math.Max(0, bounds.Width) * Math.Max(0, bounds.Height);
                candidates.Add((topLevel, key, topLevel.ProcessId, area));
            }
        }

        if (candidates.Count == 0)
        {
            // Fallback: focused element's top-level (covers offscreen-but-active windows
            // that don't appear in screen.Children for some platform backends).
            var focused = context.FocusedElement;
            if (focused is not null)
            {
                var top = WalkToTopLevel(focused);
                if (top is not null && Matches(top, app))
                {
                    var key = AppKey.FromProcessId(top.ProcessId);
                    return new ResolvedApp(top, key, top.ProcessId);
                }
            }
            return null;
        }

        var best = candidates.OrderByDescending(c => c.Area).First();
        return new ResolvedApp(best.Window, best.AppKey, best.ProcessId);
    }

    public static IReadOnlyList<ResolvedApp> ListApps(IVisualElementContext context)
    {
        // Per process, keep the LARGEST visible top-level window so the title we report is
        // the one a user would actually associate with that app (avoids reporting a menubar
        // overlay's title when the app also has a real main window). Do NOT drop menubar-only
        // apps — they are real installed programs the agent may need to drive.
        var byProcess = new Dictionary<int, (IVisualElement Window, long Area)>();
        foreach (var screen in context.Screens)
        {
            foreach (var topLevel in screen.Children)
            {
                if (topLevel.ProcessId <= 0) continue;

                var bounds = topLevel.BoundingRectangle;
                var area = (long)Math.Max(0, bounds.Width) * Math.Max(0, bounds.Height);
                if (!byProcess.TryGetValue(topLevel.ProcessId, out var existing) || area > existing.Area)
                {
                    byProcess[topLevel.ProcessId] = (topLevel, area);
                }
            }
        }

        var result = new List<ResolvedApp>();
        foreach (var (pid, (window, _)) in byProcess)
        {
            var key = AppKey.FromProcessId(pid);
            result.Add(new ResolvedApp(window, key, pid));
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
