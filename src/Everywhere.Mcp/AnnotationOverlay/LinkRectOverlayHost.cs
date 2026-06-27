using Avalonia;
using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using Everywhere.Views.Annotation;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp.AnnotationOverlay;

/// <summary>
/// Mirrors <see cref="AnnotationOverlayHost"/> but for the link-rect
/// channel. One harvest = one overlay pair: an outline framing the
/// union of every link the user dragged over, and a single ➕ at the
/// top-right where the user can write a comment that ships with the
/// links on the next SnapshotContext. We don't draw a ➕ per link —
/// rect harvests routinely produce 30+ links and per-link badges would
/// blanket the screen.
///
/// Static placement: links don't move once harvested. No follow timer.
/// </summary>
public sealed class LinkRectOverlayHost : IAsyncInitializer, IAsyncDisposable
{
    private readonly LinkRectStash _linkRectStash;
    private readonly AnnotationStash _annotationStash;
    private readonly ContextStashWriter _contextStashWriter;
    private readonly IWindowHelper _windowHelper;
    private readonly IVisualElementContext _visualContext;
    private readonly ILogger<LinkRectOverlayHost> _logger;

    private HarvestOverlayPair? _current;
    private Action<PixelRect>? _rectCommittedHandler;
    private Action<IReadOnlyList<HarvestedLink>>? _harvestedHandler;
    private Action? _captureCompletedHandler;
    private Action? _clearedHandler;
    private DispatcherTimer? _followTimer;
    private volatile bool _disposed;

    public LinkRectOverlayHost(
        LinkRectStash linkRectStash,
        AnnotationStash annotationStash,
        ContextStashWriter contextStashWriter,
        IWindowHelper windowHelper,
        IVisualElementContext visualContext,
        ILogger<LinkRectOverlayHost> logger)
    {
        _linkRectStash = linkRectStash;
        _annotationStash = annotationStash;
        _contextStashWriter = contextStashWriter;
        _windowHelper = windowHelper;
        _visualContext = visualContext;
        _logger = logger;
    }

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    public Task InitializeAsync()
    {
        _rectCommittedHandler = OnRectCommitted;
        _linkRectStash.RectCommitted += _rectCommittedHandler;

        _harvestedHandler = OnHarvested;
        _linkRectStash.Harvested += _harvestedHandler;

        _captureCompletedHandler = OnManualCaptureCompleted;
        _contextStashWriter.ManualCaptureCompleted += _captureCompletedHandler;

        _clearedHandler = OnCleared;
        _linkRectStash.Cleared += _clearedHandler;

        if (Dispatcher.UIThread.CheckAccess())
        {
            StartFollowTimer();
        }
        else
        {
            Dispatcher.UIThread.Post(StartFollowTimer);
        }

        _logger.LogInformation("LinkRectOverlayHost subscribed");
        return Task.CompletedTask;
    }

    private void StartFollowTimer()
    {
        _followTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _followTimer.Tick += OnFollowTick;
        _followTimer.Start();
    }

    private void OnFollowTick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        var pair = _current;
        if (pair?.Anchor is null) return;
        _ = RefreshAsync(pair);
    }

    private async Task RefreshAsync(HarvestOverlayPair pair)
    {
        if (Interlocked.CompareExchange(ref pair.RefreshInFlight, 1, 0) != 0) return;
        try
        {
            // Per-link follow: query each link's element bounds in one
            // background pass, then on the UI thread move each outline
            // to its current bounds. Empty bounds → keep the outline at
            // last known position (better stale than gone).
            List<(int Index, PixelRect Rect)> updates;
            try
            {
                updates = await Task.Run(() =>
                {
                    var ups = new List<(int, PixelRect)>(pair.LinkOutlines.Count);
                    for (var i = 0; i < pair.LinkOutlines.Count; i++)
                    {
                        var entry = pair.LinkOutlines[i];
                        if (entry.Element is null) continue;
                        PixelRect b;
                        try { b = entry.Element.BoundingRectangleLive; }
                        catch { continue; }
                        if (b.Width <= 0 || b.Height <= 0) continue;
                        ups.Add((i, b));
                    }
                    return ups;
                }).WaitAsync(TimeSpan.FromMilliseconds(200));
            }
            catch { return; }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || _current != pair) return;
                if (pair.LinkOutlines.Count == 0)
                {
                    // Pre-harvest fallback: still using the single drag-
                    // rect outline. Track the rect-time anchor so it
                    // scrolls with the page until harvest fills in
                    // per-link outlines.
                    return;
                }
                var union = default(PixelRect);
                var firstU = true;
                foreach (var (i, rect) in updates)
                {
                    pair.LinkOutlines[i].Outline.MoveTo(rect);
                    if (firstU) { union = rect; firstU = false; }
                    else union = union.Union(rect);
                }
                if (!firstU) pair.Badge.MoveTo(union);
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LinkRect follow refresh failed");
        }
        finally
        {
            Interlocked.Exchange(ref pair.RefreshInFlight, 0);
        }
    }

    private static PixelRect? TryGetBounds(IVisualElement element)
    {
        try
        {
            var b = element.BoundingRectangle;
            if (b.Width <= 0 || b.Height <= 0) return null;
            return b;
        }
        catch { return null; }
    }

    private void OnRectCommitted(PixelRect rect)
    {
        _logger.LogDebug(
            "LinkRectOverlayHost.OnRectCommitted fired: rect=({X},{Y},{W}x{H})",
            rect.X, rect.Y, rect.Width, rect.Height);
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            try
            {
                // New harvest in flight — drop any prior pair (its
                // annotation is orphaned).
                TearDownCurrent(removeAnnotation: true);

                if (rect.Width <= 0 || rect.Height <= 0)
                {
                    _logger.LogDebug("LinkRect rect committed with empty bounds; skipping");
                    return;
                }

                // Pick-style anchor: hit-test the rect's centre to grab
                // the element under it. The follow timer then keeps the
                // outline tracking that element (not the screen rect),
                // so the outline scrolls with the page just like Pin.
                IVisualElement? anchor = null;
                try
                {
                    // BelowOwnProcess hit-test: skips our own mask /
                    // badge windows so we get the real underlying AX
                    // leaf at the rect's centre (Pin-style).
                    var cand = _visualContext.ElementAtPointBelowOwnProcess(
                        new PixelPoint(rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
                    if (cand is not null)
                    {
                        try
                        {
                            var b = cand.BoundingRectangle;
                            var intersects = b.Width > 0 && b.Height > 0
                                && b.X < rect.Right && b.X + b.Width > rect.X
                                && b.Y < rect.Bottom && b.Y + b.Height > rect.Y;
                            // Reject screen-sized webview containers
                            // (Arc/Brave Panel) — area > 6× drag rect
                            // means we hit the wrapper, not what the
                            // user pointed at.
                            var dragArea = Math.Max(1L, (long)rect.Width * (long)rect.Height);
                            var area = (long)b.Width * (long)b.Height;
                            if (intersects && area <= dragArea * 6) anchor = cand;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "LinkRect anchor bounds check failed; using rect-only");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "LinkRect ElementFromPoint failed at rect centre");
                }

                AnnotationOutlineWindow? outline = null;
                AnnotationOverlayWindow? badge = null;
                try
                {
                    outline = new AnnotationOutlineWindow();
                    outline.ShowAt(rect);
                    _windowHelper.ConfigureAsCursorOverlay(outline);

                    badge = new AnnotationOverlayWindow();
                    var anchorInitial = anchor is not null ? TryGetBounds(anchor) : null;
                    var pair = new HarvestOverlayPair(Array.Empty<HarvestedLink>(), outline, badge)
                    {
                        Anchor = anchor,
                        DragRect = rect,
                        AnchorInitialBounds = anchorInitial,
                    };
                    pair.CommittedHandler = (_, body) => OnCommitted(pair, body);
                    pair.ClearedHandler = (_, _) => OnBadgeCleared(pair);
                    badge.Committed += pair.CommittedHandler;
                    badge.Cleared += pair.ClearedHandler;

                    badge.ShowAt(rect);
                    _windowHelper.ConfigureAsInteractiveOverlay(badge);

                    _current = pair;
                }
                catch
                {
                    try { badge?.Close(); } catch { }
                    try { outline?.Close(); } catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to show linkrect overlay (rect-only)");
            }
        });
    }

    private void OnHarvested(IReadOnlyList<HarvestedLink> links)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            if (_current is null)
            {
                _logger.LogDebug("OnHarvested but no current pair — overlay was torn down before harvest finished");
                return;
            }
            _current.Links = links;

            // Per-link outlines (matches the pre-annotation v0.9.182 flash):
            // close the single drag-rect outline, then spawn one
            // AnnotationOutlineWindow per harvested link. Each outline
            // tracks its own link.Element via the per-link follow timer
            // below. The single ➕ badge anchors at the rightmost link's
            // top-right so the user can write a comment that ships with
            // the whole batch.
            try { _current.Outline.Close(); } catch { }

            var perLinkOutlines = new List<(IVisualElement? Element, AnnotationOutlineWindow Outline, PixelRect Initial)>();
            var union = default(PixelRect);
            var firstU = true;
            foreach (var l in links)
            {
                PixelRect b;
                try { b = l.Element?.BoundingRectangle ?? l.Bounds; }
                catch { b = l.Bounds; }
                if (b.Width <= 0 || b.Height <= 0) continue;

                AnnotationOutlineWindow? linkOutline = null;
                try
                {
                    linkOutline = new AnnotationOutlineWindow();
                    linkOutline.ShowAt(b);
                    _windowHelper.ConfigureAsCursorOverlay(linkOutline);
                    perLinkOutlines.Add((l.Element, linkOutline, b));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "LinkRect per-link outline create failed");
                    try { linkOutline?.Close(); } catch { }
                }
                if (firstU) { union = b; firstU = false; }
                else union = union.Union(b);
            }

            _current.LinkOutlines = perLinkOutlines;

            if (!firstU)
            {
                // Badge anchors at the union's top-right so it's near
                // the harvest visually but doesn't overlap any single
                // link outline.
                _current.Badge.MoveTo(union);
                // Upgrade primary anchor for follow-anchor on the badge.
                var bestElement = links.Select(l => l.Element).FirstOrDefault(e => e is not null);
                if (bestElement is not null) _current.Anchor = bestElement;
                _logger.LogInformation(
                    "LinkRect spawned {Count} per-link outline(s); union=({X},{Y},{W}x{H})",
                    perLinkOutlines.Count, union.X, union.Y, union.Width, union.Height);
            }
            else
            {
                _logger.LogWarning(
                    "LinkRect harvest returned {Count} link(s) but all bounds empty; no per-link outlines",
                    links.Count);
            }
        });
    }

    private static PixelRect ComputeUnion(IReadOnlyList<HarvestedLink> links)
    {
        var union = default(PixelRect);
        var first = true;
        foreach (var h in links)
        {
            var b = h.Bounds;
            if (b.Width <= 0 || b.Height <= 0) continue;
            if (first) { union = b; first = false; }
            else union = union.Union(b);
        }
        return first ? default : union;
    }

    private void OnCommitted(HarvestOverlayPair pair, string body)
    {
        var anchorLabel = $"LinkRect harvest ({pair.Links.Count} link{(pair.Links.Count == 1 ? string.Empty : "s")})";

        if (pair.LastItem is not null)
        {
            try { _annotationStash.RemoveItem(pair.LastItem); }
            catch (Exception ex) { _logger.LogWarning(ex, "RemoveItem failed; proceeding"); }
        }

        try
        {
            var item = new AnnotationItem(
                Source: AnnotationSource.LinkRect,
                Body: body,
                AnchorRef: null,
                AnchorLabel: anchorLabel,
                CapturedAtUtc: DateTimeOffset.UtcNow);
            _annotationStash.Add(item);
            pair.LastItem = item;
            _logger.LogInformation("LinkRect annotation committed: {Anchor}: {Body}", anchorLabel, body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AnnotationStash.Add rejected linkrect annotation");
        }
    }

    private void OnBadgeCleared(HarvestOverlayPair pair)
    {
        if (pair.LastItem is null) return;
        try
        {
            _annotationStash.RemoveItem(pair.LastItem);
            pair.LastItem = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove linkrect annotation from stash");
        }
    }

    private void OnManualCaptureCompleted() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            // Annotation has just shipped via SnapshotContext — preserve the
            // stash entry, only drop the visual.
            TearDownCurrent(removeAnnotation: false);
        });

    private void OnCleared() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            // ClearStash hotkey: wipe everything, including the in-flight
            // annotation that the user explicitly asked to discard.
            TearDownCurrent(removeAnnotation: true);
        });

    private void TearDownCurrent(bool removeAnnotation)
    {
        if (_current is null) return;
        var pair = _current;
        _current = null;
        if (pair.CommittedHandler is not null) pair.Badge.Committed -= pair.CommittedHandler;
        if (pair.ClearedHandler is not null) pair.Badge.Cleared -= pair.ClearedHandler;
        if (removeAnnotation && pair.LastItem is not null)
        {
            try { _annotationStash.RemoveItem(pair.LastItem); }
            catch (Exception ex) { _logger.LogDebug(ex, "Teardown: RemoveItem failed"); }
        }
        pair.LastItem = null;
        pair.Badge.Hide();
        try { pair.Outline.Hide(); } catch { }
        try { pair.Outline.Close(); } catch { }
        foreach (var entry in pair.LinkOutlines)
        {
            try { entry.Outline.Hide(); } catch { }
            try { entry.Outline.Close(); } catch { }
        }
        pair.LinkOutlines.Clear();
        pair.Badge.Close();
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_rectCommittedHandler is not null)
        {
            _linkRectStash.RectCommitted -= _rectCommittedHandler;
            _rectCommittedHandler = null;
        }
        if (_harvestedHandler is not null)
        {
            _linkRectStash.Harvested -= _harvestedHandler;
            _harvestedHandler = null;
        }
        if (_captureCompletedHandler is not null)
        {
            _contextStashWriter.ManualCaptureCompleted -= _captureCompletedHandler;
            _captureCompletedHandler = null;
        }
        if (_clearedHandler is not null)
        {
            _linkRectStash.Cleared -= _clearedHandler;
            _clearedHandler = null;
        }
        // Process exiting; preserve any in-flight stash entry — let the
        // SnapshotContext drain logic decide whether to ship.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _followTimer?.Stop();
            _followTimer = null;
            TearDownCurrent(removeAnnotation: false);
        });
    }

    private sealed class HarvestOverlayPair
    {
        public IReadOnlyList<HarvestedLink> Links { get; set; }
        public AnnotationOutlineWindow Outline { get; }
        public AnnotationOverlayWindow Badge { get; }
        public AnnotationItem? LastItem { get; set; }
        public EventHandler<string>? CommittedHandler { get; set; }
        public EventHandler? ClearedHandler { get; set; }
        public IVisualElement? Anchor { get; set; }
        public PixelRect DragRect { get; set; }
        public PixelRect? AnchorInitialBounds { get; set; }
        public int RefreshInFlight; // 0 = idle, 1 = in flight
        // After harvest finishes we replace the single drag-rect outline
        // with one outline per link, matching the pre-annotation flash
        // visual. Outline (above) is the placeholder during harvest; once
        // populated, that one is closed and these take over.
        public List<(IVisualElement? Element, AnnotationOutlineWindow Outline, PixelRect Initial)> LinkOutlines { get; set; }
            = new();

        public HarvestOverlayPair(
            IReadOnlyList<HarvestedLink> links,
            AnnotationOutlineWindow outline,
            AnnotationOverlayWindow badge)
        {
            Links = links;
            Outline = outline;
            Badge = badge;
        }
    }
}
