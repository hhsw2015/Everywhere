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
            var anchor = pair.Anchor;
            if (anchor is null) return;
            PixelRect rect;
            try
            {
                rect = await Task.Run(() => anchor.BoundingRectangleLive)
                    .WaitAsync(TimeSpan.FromMilliseconds(50));
            }
            catch
            {
                return;
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || _current != pair) return;
                if (rect.Width <= 0 || rect.Height <= 0)
                {
                    pair.Outline.Hide();
                    pair.Badge.HideIfCollapsed();
                    return;
                }
                pair.Outline.MoveTo(rect);
                pair.Badge.MoveTo(rect);
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
                    var pair = new HarvestOverlayPair(Array.Empty<HarvestedLink>(), outline, badge)
                    {
                        Anchor = anchor,
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
            // Harvest finished after the rect-only overlay was shown.
            // Just update the pair's link list; the visual is already
            // up so we don't recreate the windows.
            if (_current is null)
            {
                _logger.LogDebug("OnHarvested but no current pair — overlay was torn down before harvest finished");
                return;
            }
            _current.Links = links;
            _logger.LogInformation("LinkRect overlay link list updated: {Count} link(s)", links.Count);
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
        pair.Outline.Hide();
        pair.Outline.Close();
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
        public int RefreshInFlight; // 0 = idle, 1 = in flight

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
