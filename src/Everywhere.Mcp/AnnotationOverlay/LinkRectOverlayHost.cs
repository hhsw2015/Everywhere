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
    private readonly ILogger<LinkRectOverlayHost> _logger;

    private HarvestOverlayPair? _current;
    private Action<IReadOnlyList<HarvestedLink>>? _harvestedHandler;
    private Action? _captureCompletedHandler;
    private Action? _clearedHandler;
    private volatile bool _disposed;

    public LinkRectOverlayHost(
        LinkRectStash linkRectStash,
        AnnotationStash annotationStash,
        ContextStashWriter contextStashWriter,
        IWindowHelper windowHelper,
        ILogger<LinkRectOverlayHost> logger)
    {
        _linkRectStash = linkRectStash;
        _annotationStash = annotationStash;
        _contextStashWriter = contextStashWriter;
        _windowHelper = windowHelper;
        _logger = logger;
    }

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    public Task InitializeAsync()
    {
        _harvestedHandler = OnHarvested;
        _linkRectStash.Harvested += _harvestedHandler;

        _captureCompletedHandler = OnManualCaptureCompleted;
        _contextStashWriter.ManualCaptureCompleted += _captureCompletedHandler;

        _clearedHandler = OnCleared;
        _linkRectStash.Cleared += _clearedHandler;

        _logger.LogInformation("LinkRectOverlayHost subscribed");
        return Task.CompletedTask;
    }

    private void OnHarvested(IReadOnlyList<HarvestedLink> links)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            try
            {
                // Replace any prior overlay — LinkRectStash is single-slot,
                // a fresh harvest supersedes the last one. The previous
                // annotation (if any) is now orphaned: its anchor is gone,
                // so drop it from the stash too. Otherwise it would ship
                // with no badge for the user to revise.
                TearDownCurrent(removeAnnotation: true);

                var union = ComputeUnion(links);
                if (union.Width <= 0 || union.Height <= 0)
                {
                    _logger.LogDebug("LinkRect harvest produced empty union; skipping overlay");
                    return;
                }

                AnnotationOutlineWindow? outline = null;
                AnnotationOverlayWindow? badge = null;
                try
                {
                    outline = new AnnotationOutlineWindow();
                    outline.ShowAt(union);
                    _windowHelper.ConfigureAsCursorOverlay(outline);

                    badge = new AnnotationOverlayWindow();
                    var pair = new HarvestOverlayPair(links, outline, badge);
                    pair.CommittedHandler = (_, body) => OnCommitted(pair, body);
                    pair.ClearedHandler = (_, _) => OnBadgeCleared(pair);
                    badge.Committed += pair.CommittedHandler;
                    badge.Cleared += pair.ClearedHandler;

                    badge.ShowAt(union);
                    _windowHelper.ConfigureAsInteractiveOverlay(badge);

                    _current = pair;
                }
                catch
                {
                    // Partial-failure cleanup: if outline/badge construction
                    // throws after a window is on screen, close it before
                    // letting the outer catch log. Otherwise translucent
                    // outlines accumulate across failed harvests.
                    try { badge?.Close(); } catch { }
                    try { outline?.Close(); } catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to show linkrect overlay");
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
        pair.Outline.Hide();
        pair.Outline.Close();
        pair.Badge.Close();
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
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
        await Dispatcher.UIThread.InvokeAsync(() => TearDownCurrent(removeAnnotation: false));
    }

    private sealed class HarvestOverlayPair
    {
        public IReadOnlyList<HarvestedLink> Links { get; }
        public AnnotationOutlineWindow Outline { get; }
        public AnnotationOverlayWindow Badge { get; }
        public AnnotationItem? LastItem { get; set; }
        public EventHandler<string>? CommittedHandler { get; set; }
        public EventHandler? ClearedHandler { get; set; }

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
