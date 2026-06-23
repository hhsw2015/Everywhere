using Avalonia;
using Avalonia.Threading;
using CoreFoundation;
using Everywhere.Interop;
using ObjCRuntime;
using HarvestedLink = Everywhere.Interop.HarvestedLink;
using HarvestResult = Everywhere.Interop.HarvestResult;

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
        /// <summary>
        /// HarvestResult: distinguishes (a) user pressed Esc/right-click vs
        /// (b) drag completed but produced zero navigable links. Caller
        /// uses this to decide whether to activate the agent app — Esc
        /// should be silent, "no links" should still flash the agent so
        /// the hotkey doesn't feel broken.
        /// </summary>
        public static async Task<HarvestResult> HarvestAsync(
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
            using var _ = cancellationToken.Register(() =>
                Dispatcher.UIThread.Post(() => window!.Close()));
            var rect = await window._rectPromise.Task;
            if (rect is null)
            {
                await Dispatcher.UIThread.InvokeAsync(window!.Close);
                return new HarvestResult(window._wasCanceled, []);
            }
            // Run the harvest while the overlay is still on screen so we
            // can flash the captured link bboxes before closing — visual
            // confirmation of "linkclump caught these N anchors". Skip the
            // flash entirely on zero results so the user doesn't sit on a
            // dim overlay for nothing.
            var harvested = await Task.Run(() => HarvestLinks(rect.Value), cancellationToken);
            try
            {
                if (harvested.Count > 0)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => window!.HighlightCapturedLinks(harvested));
                    await Task.Delay(700, cancellationToken);
                }
            }
            catch (OperationCanceledException) { /* cancel during flash is fine */ }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(window!.Close);
            }
            return new HarvestResult(false, harvested);
        }


        public void HighlightCapturedLinks(IReadOnlyList<HarvestedLink> links)
        {
            // Paint an aqua outline around every captured anchor on the
            // mask window. ScreenSelectionMaskWindow already has the
            // tooling for one rect (selection); we (ab)use the same
            // primitive by drawing each link as a thin border child.
            // Visual is best-effort — a closed window or detached canvas
            // shouldn't break the harvest path that owns the data.
            try
            {
                foreach (var mask in MaskWindows)
                    mask.SetCapturedLinkRects(links);
                if (ToolTipWindow?.ToolTip is { } tt)
                    tt.SizeInfo = $"{links.Count} link{(links.Count == 1 ? "" : "s")}";
            }
            catch (InvalidOperationException) { /* window already closed */ }
            catch (NullReferenceException) { /* mask/canvas torn down */ }
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
            // OnLeftButtonUp / OnCanceled normally complete the promise
            // first. Resolve to null here as a safety net for any close
            // path that bypasses both (window manager kill, exception in
            // OnLeftButtonUp). TrySet is a no-op once already set, so a
            // valid drag rect already in the promise survives intact.
            _rectPromise.TrySetResult(null);
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
            if (_dragRect.Width > 0 && _dragRect.Height > 0)
            {
                // Resolve the promise WITHOUT closing — HarvestAsync will
                // close after it has had a chance to paint the highlight
                // for ~700ms. Returning false keeps the overlay alive.
                _rectPromise.TrySetResult(_dragRect);
            }
            else
            {
                _rectPromise.TrySetResult(null);
            }
            return false; // don't let base session auto-close
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
            // Use a dict so the second sighting of the same URL can upgrade
            // an empty title with the better one (and pick the larger
            // bbox — usually the label vs the icon).
            var byUrl = new Dictionary<string, HarvestedLink>(StringComparer.OrdinalIgnoreCase);
            System.IO.StreamWriter? dump = null;
            if (DumpEnabled)
            {
                try
                {
                    // Per-pid + per-millisecond filename so concurrent
                    // harvests in different processes don't race on the
                    // same path. Owner-only permission (0600) — dump may
                    // contain page URLs and rendered text the user has
                    // open at the moment.
                    var path = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        $".everywhere-linkrect-dump-{Environment.ProcessId}-{DateTimeOffset.Now:yyyyMMddHHmmssfff}.txt");
                    dump = new System.IO.StreamWriter(path, append: false);
                    dump.WriteLine($"# LinkRect dump @ {DateTimeOffset.Now:O}");
                    dump.WriteLine($"# dragRect=({dragRect.X},{dragRect.Y},{dragRect.Width}x{dragRect.Height})");
                    try
                    {
                        // chmod 600 — System.IO doesn't expose this on macOS
                        // directly, fall back to syscall via File.SetUnixFileMode.
                        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }
                    catch { /* best-effort */ }
                }
                catch (System.IO.IOException) { dump = null; }
                catch (UnauthorizedAccessException) { dump = null; }
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
                    WalkAndHarvest(app, dragRect, byUrl, depth: 0, budget);
                }
                dump?.WriteLine($"# kept={byUrl.Count}");
            }
            finally
            {
                _walkDump = null;
                dump?.Dispose();
            }
            return byUrl.Values.ToList();
        }

        private const int MaxDepth = 60;
        private sealed class WalkBudget { public int Remaining; }

        private static void WalkAndHarvest(
            IVisualElement node,
            PixelRect dragRect,
            Dictionary<string, HarvestedLink> byUrl,
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
                    string? dumpUrl = null, dumpName = null, dumpDesc = null, dumpVal = null;
                    try { dumpUrl = node.Url; } catch { }
                    try { dumpName = node.Name; } catch { }
                    if (node is AXUIElement axDump)
                    {
                        try { dumpDesc = axDump.Description; } catch { }
                    }
                    try { dumpVal = node.GetText(maxLength: 60); } catch { }
                    _walkDump.WriteLine(
                        $"  link bbox=({bounds.X},{bounds.Y},{bounds.Width}x{bounds.Height}) " +
                        $"inside={inside} url={dumpUrl ?? "(null)"} " +
                        $"name=\"{dumpName ?? string.Empty}\" " +
                        $"desc=\"{dumpDesc ?? string.Empty}\" " +
                        $"value=\"{dumpVal ?? string.Empty}\"");
                }
                if (inside)
                {
                    var url = node.Url;
                    // javascript: rescue — sites like xlinkBook popup wire
                    // links as <a href="javascript:void(0)">https://real/url</a>
                    // with the destination as the anchor's accessible label.
                    // Different browsers/sites surface that label on
                    // different AX channels: Name (innerText), Description
                    // (aria-label), Value (input-shaped). Try them all in
                    // turn, then ancestor row text as a last resort.
                    // Only fires when AXURL is a non-navigable scheme so
                    // well-behaved sites are untouched.
                    var rescuedExtras = (List<string>?)null;
                    if (!string.IsNullOrEmpty(url) && !IsAllowedScheme(url))
                    {
                        // First non-empty candidate that yields any http URL
                        // wins — note xlinkBook may pack multiple URLs into
                        // one aria-label as "url1*url2*url3", so harvest ALL
                        // matches from that string and add the extras to
                        // byUrl directly (the primary takes the anchor's
                        // bbox/title slot).
                        foreach (var probe in EnumerateRescueCandidates(node))
                        {
                            if (string.IsNullOrWhiteSpace(probe)) continue;
                            var all = ExtractAllHttpUrls(probe!);
                            if (all.Count == 0) continue;
                            url = all[0];
                            if (all.Count > 1)
                                rescuedExtras = all.GetRange(1, all.Count - 1);
                            break;
                        }
                    }
                    if (!string.IsNullOrEmpty(url)
                        && url!.Length <= 2048
                        && IsAllowedScheme(url))
                    {
                        // AX hyperlink label fallback chain:
                        //   1. AXTitle  (Name)        — plain <a>text</a>
                        //   2. AXDescription          — svg-icon anchors with aria-label
                        //   3. AXValue  (GetText)     — input-style anchors
                        //   4. Ancestor row text      — sites where the anchor is
                        //      a 16x28 svg icon and the human-readable label sits
                        //      on a sibling/parent <li> (xlinkBook popup, GitHub
                        //      repo row). Walk up ≤3 parents looking for visible
                        //      text bigger than the anchor itself.
                        var title = node.Name;
                        if (string.IsNullOrWhiteSpace(title)
                            && node is AXUIElement axNode)
                            title = axNode.Description;
                        if (string.IsNullOrWhiteSpace(title))
                            title = node.GetText(maxLength: 200);
                        if (string.IsNullOrWhiteSpace(title))
                            title = AncestorRowText(node, depth: 3);
                        if (title is not null && title.Length > 200) title = title[..200];
                        // Drop tiny icon-only anchors with no title — these
                        // are usually utility icons (copy / open-in-new-tab
                        // / share) on row-click sites. We keep tiny anchors
                        // with titles (GitHub release asset svg+label combos
                        // get titles via the AXDescription / row-text
                        // fallback chain).
                        // Treat anchor as an icon-only nav element when
                        // both axes are small AND no title surfaces from
                        // any AX channel. Using OR here would false-drop
                        // wide-but-short text anchors (e.g. text-only
                        // breadcrumb at 220x18 on a list page).
                        var isUntitledIcon = string.IsNullOrEmpty(title)
                            && bounds.Width <= 32 && bounds.Height <= 32;
                        if (!isUntitledIcon)
                        {
                            AddOrUpgrade(byUrl, new HarvestedLink(title ?? string.Empty, url, bounds));
                        }
                        // Additional URLs packed into the same aria-label
                        // (xlinkBook "url1*url2*url3" pattern) get their own
                        // entries — we share the anchor bbox since they all
                        // sit on the same row in the source markup.
                        if (rescuedExtras is not null)
                        {
                            foreach (var extra in rescuedExtras)
                            {
                                if (string.IsNullOrEmpty(extra) || extra.Length > 2048) continue;
                                if (!IsAllowedScheme(extra)) continue;
                                AddOrUpgrade(byUrl, new HarvestedLink(extra, extra, bounds));
                            }
                        }
                    }
                }
            }

            IEnumerable<IVisualElement> children;
            try { children = node.Children; }
            catch { return; }
            foreach (var c in children)
                WalkAndHarvest(c, dragRect, byUrl, depth + 1, budget);
        }

        private static bool IntersectsLoose(PixelRect a, PixelRect b)
        {
            return !(a.Right < b.X || a.X > b.Right || a.Bottom < b.Y || a.Y > b.Bottom);
        }

        /// <summary>
        /// For svg-icon anchors with no AX label, climb up to <paramref name="depth"/>
        /// ancestors and return the first non-empty visible text. Helps xlinkBook
        /// popup rows / GitHub repo rows where the link text lives on a parent.
        /// </summary>
        private static string? AncestorRowText(IVisualElement node, int depth)
        {
            try
            {
                var cur = node;
                for (var i = 0; i < depth; i++)
                {
                    cur = cur.Parent;
                    if (cur is null) return null;
                    string? name = null;
                    try { name = cur.Name; } catch { }
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                    if (cur is AXUIElement axCur)
                    {
                        string? desc = null;
                        try { desc = axCur.Description; } catch { }
                        if (!string.IsNullOrWhiteSpace(desc)) return desc;
                    }
                    string? text = null;
                    try { text = cur.GetText(maxLength: 200); } catch { }
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
            catch { /* ignore */ }
            return null;
        }

        // Reject javascript:/data:/file:/vbscript: etc that the agent might
        // execute or fetch later. Only let through navigable web schemes.
        private static bool IsAllowedScheme(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u))
                return u.Scheme is "http" or "https" or "mailto";
            return false;
        }

        // Pull the first http(s) URL out of a string. Used to rescue real
        // URLs from anchors whose href is javascript: but whose visible
        // text is the actual destination (xlinkBook popup pattern).
        // Stops at whitespace, brackets/quotes, AND '*' — xlinkBook joins
        // multiple URLs in a single aria-label as "url1*url2*url3" so a
        // permissive regex would swallow all three as one giant URL.
        private static readonly System.Text.RegularExpressions.Regex _httpUrlRegex =
            new(@"https?://[^\s<>""'*]+", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static void AddOrUpgrade(Dictionary<string, HarvestedLink> byUrl, HarvestedLink link)
        {
            if (byUrl.TryGetValue(link.Url, out var existing))
            {
                var existingScore = (string.IsNullOrEmpty(existing.Title) ? 0 : 100)
                                    + existing.Bounds.Width * existing.Bounds.Height;
                var newScore = (string.IsNullOrEmpty(link.Title) ? 0 : 100)
                               + link.Bounds.Width * link.Bounds.Height;
                if (newScore > existingScore) byUrl[link.Url] = link;
            }
            else
            {
                byUrl[link.Url] = link;
            }
        }

        // Pulls every http(s) URL out of a string. Used for the xlinkBook
        // multi-link aria-label pattern "url1*url2*url3"; falls back to a
        // single match for normal anchors. @owner/repo shorthand is checked
        // separately when the regex finds nothing.
        private static List<string> ExtractAllHttpUrls(string text)
        {
            var list = new List<string>(2);
            foreach (System.Text.RegularExpressions.Match m in _httpUrlRegex.Matches(text))
            {
                var v = m.Value.TrimEnd('.', ',', ';');
                while (v.EndsWith(')') && v.Count(c => c == '(') < v.Count(c => c == ')'))
                    v = v[..^1];
                while (v.EndsWith(']') && v.Count(c => c == '[') < v.Count(c => c == ']'))
                    v = v[..^1];
                if (!string.IsNullOrEmpty(v)) list.Add(v);
            }
            if (list.Count > 0) return list;
            // Fall through to @owner/repo expansion only when no http URL
            // was found.
            if (TryExtractHttpUrl(text, out var single)) list.Add(single);
            return list;
        }

        private static IEnumerable<string?> EnumerateRescueCandidates(IVisualElement node)
        {
            string? v;
            try { v = node.Name; } catch { v = null; }
            yield return v;
            if (node is AXUIElement ax)
            {
                try { v = ax.Description; } catch { v = null; }
                yield return v;
            }
            try { v = node.GetText(maxLength: 2048); } catch { v = null; }
            yield return v;
        }

        // GitHub-style @owner/repo shorthand used by xlinkBook popup as
        // javascript:void anchor labels. Only expand when both owner and
        // repo are present — bare `@name` is ambiguous (could be user,
        // org, or repo) and would synthesize a wrong URL more often than
        // not. Bare-handle anchors are simply dropped (handled by the
        // scheme filter) so the agent can rely on stash entries pointing
        // at real navigable targets.
        private static readonly System.Text.RegularExpressions.Regex _githubAtRegex =
            new(@"^@([A-Za-z0-9](?:[A-Za-z0-9\-_.]{0,38}))/([A-Za-z0-9_.\-]+)$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static bool TryExtractHttpUrl(string text, out string url)
        {
            var m = _httpUrlRegex.Match(text);
            if (m.Success)
            {
                var v = m.Value;
                v = v.TrimEnd('.', ',', ';');
                while (v.EndsWith(')') && v.Count(c => c == '(') < v.Count(c => c == ')'))
                    v = v[..^1];
                while (v.EndsWith(']') && v.Count(c => c == '[') < v.Count(c => c == ']'))
                    v = v[..^1];
                url = v;
                return true;
            }
            // Site-specific shorthand: xlinkBook popup writes GitHub repos
            // as `@owner/repo`. Expand to a real github.com URL. Bare
            // `@name` is intentionally rejected — see _githubAtRegex doc.
            var trimmed = text.Trim();
            var gh = _githubAtRegex.Match(trimmed);
            if (gh.Success)
            {
                url = "https://github.com/" + gh.Groups[1].Value + "/" + gh.Groups[2].Value;
                return true;
            }
            url = string.Empty;
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
