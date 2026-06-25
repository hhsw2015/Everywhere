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
    // Per-pid memoization for TryEnableBestEffortAccessibility.
    // The enable call sets AXManualAccessibility + AXEnhancedUserInterface,
    // which on apps like Notes / Finder / Chromium triggers a full AX
    // subsystem rebuild — tens of seconds the first time. Calling it on
    // every Resolve compounded that on every click. OCCU does it once
    // per snapshot lifetime; we do it once per process lifetime here.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _a11yEnabledPids = new();

    private static void EnsureA11yEnabledOnce(IVisualElementContext context, int pid)
    {
        if (pid <= 0) return;
        if (!_a11yEnabledPids.TryAdd(pid, true))
        {
            LogStep("    [a11y] cache hit, skipped", System.Environment.TickCount64);
            return;
        }
        LogStep("    [a11y] cache miss, will call SetAttribute(AXManualAccessibility)", System.Environment.TickCount64);
        // CRITICAL: AXUIElementSetAttributeValue(AXManualAccessibility)
        // is synchronous and blocking. Notes / Finder / Electron etc
        // can take 30+s the first time it fires (full AX subsystem
        // rebuild, no way to cancel). That's the "几分钟" hang —
        // it's BEFORE Walk so the deadline can't help.
        //
        // Cap it at 1.5s. The call is fired on the thread pool with a
        // bounded wait; if it doesn't return in time we abandon the
        // wait and continue. The system's still working on the rebuild
        // in the background, so the NEXT click on the same pid will
        // see the upgraded tree. SwiftUI Calculator-class apps still
        // get enhanced AX on the second call.
        var taskT = System.Environment.TickCount64;
        var task = System.Threading.Tasks.Task.Run(() =>
        {
            var inner = System.Environment.TickCount64;
            try { context.TryEnableBestEffortAccessibility(pid); } catch { /* best-effort */ }
            LogStep($"    [a11y] inner SetAttribute returned (or threw)", inner);
        });
        try { task.Wait(System.TimeSpan.FromMilliseconds(1500)); }
        catch { /* timeout / aggregation — abandon. */ }
        LogStep("    [a11y] task.Wait(1500ms) returned", taskT);
    }

    public readonly record struct ResolvedApp(IVisualElement Window, string AppKey, int ProcessId);

    private static void LogStep(string label, long t0)
    {
        var ms = System.Environment.TickCount64 - t0;
        try
        {
            System.IO.File.AppendAllText("/tmp/everywhere-perf.log",
                $"[{System.DateTime.Now:HH:mm:ss.fff}]   {label} took {ms}ms\n");
        }
        catch { }
    }

    public static ResolvedApp? Resolve(IVisualElementContext context, string app)
    {
        if (string.IsNullOrWhiteSpace(app))
        {
            return null;
        }
        var rt = System.Environment.TickCount64;

        // Build a list of candidate query strings: original hint plus any category aliases
        // ("browser" → arc/safari/chrome/...) so a generic noun resolves to whichever
        // concrete app the user has open.
        var queries = new List<string> { app };
        queries.AddRange(AppAliases.Expand(app));

        var candidates = new List<(IVisualElement Window, string AppKey, int ProcessId, long Area)>();
        var stepT = System.Environment.TickCount64;

        // Fast path: ask the platform context for the candidate pid by
        // name (NSWorkspace.runningApplications, etc) — bypasses
        // enumerating every other app's AX tree, which is what made
        // Notes/Finder Resolve cost 10-30s on machines with many apps
        // open. Falls back to the screen walk only if the platform
        // can't provide a fast lookup.
        IVisualElement? fastWindow = null;
        int fastPid = 0;
        foreach (var q in queries)
        {
            if (context.TryFastResolveByName(q) is { } hit)
            {
                fastWindow = hit;
                fastPid = hit.ProcessId;
                break;
            }
        }
        if (fastWindow is not null)
        {
            var key = AppKey.FromProcessId(fastPid);
            var bounds = fastWindow.BoundingRectangle;
            var area = (long)Math.Max(0, bounds.Width) * Math.Max(0, bounds.Height);
            candidates.Add((fastWindow, key, fastPid, area));
            LogStep($"  Resolve.fastResolve(pid={fastPid})", stepT);
        }
        else
        {
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
            LogStep($"  Resolve.scanCandidates(found={candidates.Count})", stepT);
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
                    EnsureA11yEnabledOnce(context, top.ProcessId);
                    var freshTop = context.FreshFocusedWindowOf(top.ProcessId) ?? top;
                    return new ResolvedApp(freshTop, key, top.ProcessId);
                }
            }
            return null;
        }

        var best = candidates.OrderByDescending(c => c.Area).First();
        var enableT = System.Environment.TickCount64;
        EnsureA11yEnabledOnce(context, best.ProcessId);
        LogStep("  Resolve.EnsureA11yEnabledOnce", enableT);
        var freshT = System.Environment.TickCount64;
        var freshWindow = context.FreshFocusedWindowOf(best.ProcessId) ?? best.Window;
        LogStep("  Resolve.FreshFocusedWindowOf", freshT);
        return new ResolvedApp(freshWindow, best.AppKey, best.ProcessId);
    }

    public static IReadOnlyList<ResolvedApp> ListApps(IVisualElementContext context)
    {
        // Fast path via NSWorkspace; bypasses the per-app AX subtree walks
        // that made the original Screens.Children path cost 10-30s.
        var fast = context.TryFastListApps();
        if (fast.Count > 0)
        {
            var resultFast = new List<ResolvedApp>(fast.Count);
            foreach (var (window, pid) in fast)
            {
                resultFast.Add(new ResolvedApp(window, AppKey.FromProcessId(pid), pid));
            }
            return resultFast;
        }

        // Fallback (Windows backend / Mac edge cases).
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
