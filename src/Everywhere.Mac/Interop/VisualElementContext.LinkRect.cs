using Avalonia;
using Avalonia.Threading;
using CoreFoundation;
using Everywhere.Interop;
using ObjCRuntime;
using HarvestedLink = Everywhere.Interop.HarvestedLink;

namespace Everywhere.Mac.Interop;

partial class VisualElementContext
{
    /// <summary>
    /// linkclump-plus style harvest: drag a rectangle over a page, collect
    /// every Hyperlink element whose bounds intersect the rect, return the
    /// (Name, Url) batch. The downstream perception path (xlinkBook
    /// /url_cache/get_bulk) takes it from there.
    /// </summary>
    private sealed class LinkRectSession : ScreenSelectionSession
    {
        public static async Task<IReadOnlyList<HarvestedLink>> HarvestAsync(
            IWindowHelper windowHelper, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            LinkRectSession? window = null;
            try
            {
                window = new LinkRectSession(windowHelper);
                window.Show();
            }
            catch (Exception ex)
            {
                window?._rectPromise.TrySetException(ex);
                throw;
            }
            // Cancellation closes the overlay so OnClosed -> _rectPromise
            // resolves with null instead of leaving the await hanging.
            using var _ = cancellationToken.Register(() =>
                Dispatcher.UIThread.Post(() => window!.Close()));
            var rect = await window._rectPromise.Task;
            if (rect is null) return [];
            return await Task.Run(() => HarvestLinks(rect.Value), cancellationToken);
        }

        private readonly TaskCompletionSource<PixelRect?> _rectPromise = new();

        private bool _isDragging;
        private bool _wasCanceled;
        private CGPoint _dragStart;
        private PixelRect _dragRect;

        private LinkRectSession(IWindowHelper windowHelper)
            : base(windowHelper, [ScreenSelectionMode.LinkRect], ScreenSelectionMode.LinkRect)
        {
        }

        protected override void OnCanceled()
        {
            base.OnCanceled();
            _wasCanceled = true;
        }

        protected override void OnClosed(EventArgs e)
        {
            // Distinguish cancel (Esc/right-click) from "dragged but no
            // rect / no links" — caller gets null vs empty list.
            if (_wasCanceled || _dragRect.Width <= 0 || _dragRect.Height <= 0)
                _rectPromise.TrySetResult(null);
            else
                _rectPromise.TrySetResult(_dragRect);
            base.OnClosed(e);
        }

        protected override void OnLeftButtonDown()
        {
            var primaryScreenHeight = NSScreen.Screens[0].Frame.Height;
            var quartzStart = new CGPoint(CurrentMouseLocation.X, primaryScreenHeight - CurrentMouseLocation.Y);
            _dragStart = quartzStart;
            _isDragging = true;
            _dragRect = new PixelRect((int)quartzStart.X, (int)quartzStart.Y, 0, 0);
            foreach (var mask in MaskWindows) mask.SetMask(_dragRect);
        }

        protected override bool OnLeftButtonUp()
        {
            if (!_isDragging) return false;
            _isDragging = false;
            // Don't harvest here — closes the overlay first, harvest runs
            // off-thread in HarvestAsync after _rectPromise resolves.
            return _dragRect.Width > 0 && _dragRect.Height > 0;
        }

        protected override void OnMove(CGPoint point)
        {
            if (!_isDragging)
            {
                foreach (var mask in MaskWindows) mask.SetMask(new PixelRect(0, 0, 0, 0));
                return;
            }
            var minX = Math.Min(_dragStart.X, point.X);
            var minY = Math.Min(_dragStart.Y, point.Y);
            var maxX = Math.Max(_dragStart.X, point.X);
            var maxY = Math.Max(_dragStart.Y, point.Y);
            _dragRect = new PixelRect((int)minX, (int)minY, (int)(maxX - minX), (int)(maxY - minY));
            foreach (var mask in MaskWindows) mask.SetMask(_dragRect);
            UpdateToolTipInfo(_dragRect);
        }

        // EVERYWHERE_LINKRECT_DUMP=1 -> writes a per-app diagnostic file to
        // ~/.everywhere-linkrect-dump.txt with every Hyperlink AX node we
        // saw + whether bounds intersected the drag rect. Lets the user
        // compare "what I framed visually" vs "what AX actually exposed".
        private static readonly bool DumpEnabled =
            Environment.GetEnvironmentVariable("EVERYWHERE_LINKRECT_DUMP") == "1";

        // Thread-local so the inner walk can write without threading a
        // StreamWriter param through every recursion frame.
        [ThreadStatic]
        private static System.IO.StreamWriter? _walkDump;

        private static List<HarvestedLink> HarvestLinks(PixelRect dragRect)
        {
            // Walk every visible window's AX tree, keep Hyperlink elements
            // whose bounds intersect the drag rect. De-dup by URL so one
            // anchor that surfaces multiple times (icon + label) lands once.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<HarvestedLink>(64);
            System.IO.StreamWriter? dump = null;
            if (DumpEnabled)
            {
                try
                {
                    var path = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".everywhere-linkrect-dump.txt");
                    dump = new System.IO.StreamWriter(path, append: false);
                    dump.WriteLine($"# LinkRect dump @ {DateTimeOffset.Now:O}");
                    dump.WriteLine($"# dragRect=({dragRect.X},{dragRect.Y},{dragRect.Width}x{dragRect.Height})");
                }
                catch { dump = null; }
            }

            // Enumerate processes that own on-screen windows whose bounds
            // intersect the drag rect — walks each candidate AX tree once.
            var pids = new HashSet<int>();
            CollectAllOnScreenPids(pids, dragRect);

            _walkDump = dump;
            try
            {
                var budget = new WalkBudget { Remaining = 50_000 };
                foreach (var pid in pids)
                {
                    if (budget.Remaining <= 0) break;
                    if (AXUIElement.ElementFromPid(pid) is not { } app) continue;
                    dump?.WriteLine($"--- pid={pid} ---");
                    WalkAndHarvest(app, dragRect, seen, result, depth: 0, budget);
                }
                dump?.WriteLine($"# kept={result.Count}");
            }
            finally
            {
                _walkDump = null;
                dump?.Dispose();
            }
            return result;
        }

        private const int MaxDepth = 60;
        private sealed class WalkBudget { public int Remaining; }

        private static void WalkAndHarvest(
            IVisualElement node,
            PixelRect dragRect,
            HashSet<string> seen,
            List<HarvestedLink> result,
            int depth,
            WalkBudget budget)
        {
            if (depth > MaxDepth) return;
            if (budget.Remaining-- <= 0) return;
            PixelRect bounds;
            try { bounds = node.BoundingRectangle; }
            catch { return; }
            if (bounds.Width > 0 && bounds.Height > 0 && !IntersectsLoose(bounds, dragRect))
                return;

            if (node.Type == VisualElementType.Hyperlink)
            {
                // Only emit if the hyperlink itself overlaps the drag rect.
                // We descend into 0/0-bound nodes (lazy AX subtrees) but a
                // 0-bound link should never count as "inside the rect".
                var inside = bounds.Width > 0 && bounds.Height > 0
                             && IntersectsLoose(bounds, dragRect);
                if (_walkDump != null)
                {
                    string? dumpUrl = null, dumpName = null;
                    try { dumpUrl = node.Url; } catch { }
                    try { dumpName = node.Name; } catch { }
                    _walkDump.WriteLine($"  link bbox=({bounds.X},{bounds.Y},{bounds.Width}x{bounds.Height}) inside={inside} url={dumpUrl ?? "(null)"} name=\"{dumpName ?? string.Empty}\"");
                }
                if (inside)
                {
                    var url = node.Url;
                    if (!string.IsNullOrEmpty(url)
                        && url!.Length <= 2048
                        && IsAllowedScheme(url))
                    {
                        // AX hyperlink label fallback chain:
                        //   AXTitle  (Name) — usually filled for plain <a>text</a>
                        //   AXDescription   — Safari/Chrome put svg/icon-only
                        //                     anchor text here (e.g. github
                        //                     release asset rows)
                        //   AXValue   (GetText) — last resort
                        // Without the Description rung, GitHub release pages
                        // and other svg-anchor sites harvest with empty titles.
                        var title = node.Name;
                        if (string.IsNullOrWhiteSpace(title)
                            && node is AXUIElement axNode)
                            title = axNode.Description;
                        if (string.IsNullOrWhiteSpace(title))
                            title = node.GetText(maxLength: 200);
                        if (title is not null && title.Length > 200) title = title[..200];
                        var key = url + "\0" + (title ?? string.Empty);
                        if (seen.Add(key))
                            result.Add(new HarvestedLink(title ?? string.Empty, url, bounds));
                    }
                }
            }

            IEnumerable<IVisualElement> children;
            try { children = node.Children; }
            catch { return; }
            foreach (var c in children)
                WalkAndHarvest(c, dragRect, seen, result, depth + 1, budget);
        }

        private static bool IntersectsLoose(PixelRect a, PixelRect b)
        {
            return !(a.Right < b.X || a.X > b.Right || a.Bottom < b.Y || a.Y > b.Bottom);
        }

        // Reject javascript:/data:/file:/vbscript: etc that the agent might
        // execute or fetch later. Only let through navigable web schemes.
        private static bool IsAllowedScheme(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u))
                return u.Scheme is "http" or "https" or "mailto";
            return false;
        }

        private static void CollectAllOnScreenPids(HashSet<int> pids, PixelRect dragRect)
        {
            // Only enumerate apps whose visible windows actually intersect
            // the drag rect — saves walking 50+ unrelated app AX trees.
            var info = CGInterop.CGWindowListCopyWindowInfo(CGWindowListOption.OnScreenOnly, 0);
            if (info == 0) return;
            using var array = Runtime.GetNSObject<NSArray>(info, owns: true);
            if (array is null) return;
            using var kPid    = new NSString("kCGWindowOwnerPID");
            using var kBounds = new NSString("kCGWindowBounds");
            for (nuint i = 0; i < (nuint)array.Count; i++)
            {
                using var dict = array.GetItem<NSDictionary>(i);
                if (dict is null) continue;
                if (dict.ObjectForKey(kPid) is not NSNumber n) continue;
                // Best-effort window bounds intersect — if missing/parse
                // fails, fall through and include the pid (avoid losing
                // links from apps with unusual window descriptors).
                if (dict.ObjectForKey(kBounds) is NSDictionary bDict
                    && TryReadWindowRect(bDict, out var winRect)
                    && !IntersectsLoose(winRect, dragRect))
                    continue;
                pids.Add(n.Int32Value);
            }
        }

        private static bool TryReadWindowRect(NSDictionary dict, out PixelRect rect)
        {
            rect = default;
            using var kX = new NSString("X");
            using var kY = new NSString("Y");
            using var kW = new NSString("Width");
            using var kH = new NSString("Height");
            if (dict.ObjectForKey(kX) is not NSNumber x ||
                dict.ObjectForKey(kY) is not NSNumber y ||
                dict.ObjectForKey(kW) is not NSNumber w ||
                dict.ObjectForKey(kH) is not NSNumber h) return false;
            rect = new PixelRect(x.Int32Value, y.Int32Value, w.Int32Value, h.Int32Value);
            return true;
        }
    }

}
