using Avalonia;
using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.Interop.Whiteboard;
using Everywhere.Mcp.Snapshot;
using Everywhere.Views.Annotation;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp.AnnotationOverlay;

/// <summary>
/// Mirrors <see cref="AnnotationOverlayHost"/> but for the whiteboard
/// channel. Each freshly-drawn region gets its own outline + ➕ pair.
/// When the region carries at least one leaf element, the host runs the
/// same 50ms follow loop as the Pin path: outline tracks the leaf's
/// BoundingRectangleLive so it stays anchored as the user scrolls. When
/// the region has no leaves (Chromium webview fallback path), the
/// outline anchors to the gesture rect and is static — best we can do
/// without an a11y handle.
/// </summary>
public sealed class WhiteboardOverlayHost : IAsyncInitializer, IAsyncDisposable
{
    private readonly WhiteboardStash _whiteboardStash;
    private readonly AnnotationStash _annotationStash;
    private readonly ContextStashWriter _contextStashWriter;
    private readonly IWindowHelper _windowHelper;
    private readonly ILogger<WhiteboardOverlayHost> _logger;

    private readonly List<RegionOverlayPair> _overlays = new();
    private Action<IReadOnlyList<WhiteboardRegion>>? _drawnHandler;
    private Action? _captureCompletedHandler;
    private Action? _clearedHandler;
    private DispatcherTimer? _followTimer;
    private volatile bool _disposed;

    public WhiteboardOverlayHost(
        WhiteboardStash whiteboardStash,
        AnnotationStash annotationStash,
        ContextStashWriter contextStashWriter,
        IWindowHelper windowHelper,
        ILogger<WhiteboardOverlayHost> logger)
    {
        _whiteboardStash = whiteboardStash;
        _annotationStash = annotationStash;
        _contextStashWriter = contextStashWriter;
        _windowHelper = windowHelper;
        _logger = logger;
    }

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    public Task InitializeAsync()
    {
        _drawnHandler = OnDrawn;
        _whiteboardStash.Drawn += _drawnHandler;

        _captureCompletedHandler = OnManualCaptureCompleted;
        _contextStashWriter.ManualCaptureCompleted += _captureCompletedHandler;

        _clearedHandler = OnCleared;
        _whiteboardStash.Cleared += _clearedHandler;

        // Synchronous timer init avoids a race where Drawn fires before
        // the timer Post executes, leaving regions without follow-tracking
        // until the dispatcher catches up.
        if (Dispatcher.UIThread.CheckAccess())
        {
            StartFollowTimer();
        }
        else
        {
            Dispatcher.UIThread.Post(StartFollowTimer);
        }

        _logger.LogInformation("WhiteboardOverlayHost subscribed");
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
        if (_overlays.Count == 0) return;
        foreach (var pair in _overlays)
        {
            if (pair.Region.Leaves.Count == 0) continue;
            _ = RefreshPairAsync(pair);
        }
    }

    private async Task RefreshPairAsync(RegionOverlayPair pair)
    {
        if (Interlocked.CompareExchange(ref pair.RefreshInFlight, 1, 0) != 0) return;
        try
        {
            var leaf = pair.Region.Leaves[0];
            PixelRect rect;
            try
            {
                rect = await Task.Run(() => leaf.BoundingRectangleLive)
                    .WaitAsync(TimeSpan.FromMilliseconds(50));
            }
            catch
            {
                return;
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed) return;
                if (!_overlays.Contains(pair)) return;
                // Empty bounds = anchor element doesn't expose live geometry
                // (Chromium Hyperlink ancestor, etc). Don't hide — keep the
                // overlay at its original draw rect so the user retains a
                // visual marker. Better-than-nothing fallback when AX is
                // useless for follow tracking.
                if (rect.Width <= 0 || rect.Height <= 0) return;
                pair.Outline.MoveTo(rect);
                pair.Badge.MoveTo(rect);
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Whiteboard follow refresh failed");
        }
        finally
        {
            Interlocked.Exchange(ref pair.RefreshInFlight, 0);
        }
    }

    private void OnDrawn(IReadOnlyList<WhiteboardRegion> regions)
    {
        _logger.LogInformation("WhiteboardOverlayHost.OnDrawn fired with {Count} region(s)", regions.Count);
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            try
            {
                // Defensive dedup: keyed on region instance so a future
                // change that re-emits the same WhiteboardRegion doesn't
                // produce stacked overlays. Distinct regions that resolve
                // to the same anchor rect each get their own ➕.
                var existing = _overlays.Select(p => p.Region).ToHashSet(ReferenceEqualityComparer.Instance);
                foreach (var region in regions)
                {
                    if (existing.Contains(region)) continue;
                    var rect = ResolveAnchorRect(region);
                    _logger.LogInformation(
                        "WhiteboardOverlay region: kind={Kind} regionRect=({X},{Y},{W}x{H}) leaves={Leaves} anchorRect=({Ax},{Ay},{Aw}x{Ah})",
                        region.Kind, region.Rect.X, region.Rect.Y, region.Rect.Width, region.Rect.Height,
                        region.Leaves.Count, rect.X, rect.Y, rect.Width, rect.Height);
                    if (rect.Width <= 0 || rect.Height <= 0)
                    {
                        _logger.LogWarning("WhiteboardOverlay: skipping region with empty rect");
                        continue;
                    }

                    AnnotationOutlineWindow? outline = null;
                    AnnotationOverlayWindow? badge = null;
                    try
                    {
                        outline = new AnnotationOutlineWindow();
                        outline.ShowAt(rect);
                        _windowHelper.ConfigureAsCursorOverlay(outline);

                        badge = new AnnotationOverlayWindow();
                        var pair = new RegionOverlayPair(region, outline, badge);
                        pair.CommittedHandler = (_, body) => OnCommitted(pair, body);
                        pair.ClearedHandler = (_, _) => OnBadgeCleared(pair);
                        badge.Committed += pair.CommittedHandler;
                        badge.Cleared += pair.ClearedHandler;

                        // Show & configure BEFORE adding to _overlays, so
                        // a failure here doesn't leave a half-initialized
                        // pair behind for TearDown to swing at.
                        badge.ShowAt(rect);
                        _windowHelper.ConfigureAsInteractiveOverlay(badge);
                        _overlays.Add(pair);
                    }
                    catch
                    {
                        try { badge?.Close(); } catch { }
                        try { outline?.Close(); } catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to show whiteboard overlay");
            }
        });
    }

    /// <summary>
    /// Anchor rect resolution. When the region carries leaves, use their
    /// union bbox — that's the actual element the user pointed at, and
    /// the follow timer will keep it tracking. When there are no leaves
    /// (extreme fallback path), use the gesture rect.
    /// Both leaves' BoundingRectangle and the gesture rect live in the
    /// SAME coordinate space (DIP/points on macOS, physical px on
    /// Windows; see WhiteboardOverlay.Commit's coord-space note).
    /// </summary>
    private PixelRect ResolveAnchorRect(WhiteboardRegion region)
    {
        if (region.Leaves.Count > 0)
        {
            var union = default(PixelRect);
            var first = true;
            foreach (var leaf in region.Leaves)
            {
                PixelRect b;
                try { b = leaf.BoundingRectangle; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Whiteboard leaf BoundingRectangle threw; skipping");
                    continue;
                }
                if (b.Width <= 0 || b.Height <= 0) continue;
                if (first) { union = b; first = false; }
                else union = union.Union(b);
            }
            if (!first) return union;
        }

        return new PixelRect(
            (int)Math.Round(region.Rect.X),
            (int)Math.Round(region.Rect.Y),
            (int)Math.Round(region.Rect.Width),
            (int)Math.Round(region.Rect.Height));
    }

    private void OnCommitted(RegionOverlayPair pair, string body)
    {
        string anchorLabel;
        try { anchorLabel = BuildAnchorLabel(pair.Region); }
        catch (Exception ex)
        {
            // BuildAnchorLabel touches Leaves[0].Type which can throw on
            // stale AX/UIA elements. Don't drop the user's commit just
            // because the label is unavailable — fall back to the basic
            // size-only label that doesn't touch leaves.
            _logger.LogDebug(ex, "BuildAnchorLabel threw; using fallback");
            var size = $"{(int)Math.Round(pair.Region.Rect.Width)}x{(int)Math.Round(pair.Region.Rect.Height)}";
            anchorLabel = $"{pair.Region.Kind} gesture ({size})";
        }

        if (pair.LastItem is not null)
        {
            try { _annotationStash.RemoveItem(pair.LastItem); }
            catch (Exception ex) { _logger.LogWarning(ex, "RemoveItem failed; proceeding"); }
        }

        try
        {
            var item = new AnnotationItem(
                Source: AnnotationSource.Whiteboard,
                Body: body,
                AnchorRef: null,
                AnchorLabel: anchorLabel,
                CapturedAtUtc: DateTimeOffset.UtcNow);
            _annotationStash.Add(item);
            pair.LastItem = item;
            _logger.LogInformation("Whiteboard annotation committed: {Anchor}: {Body}", anchorLabel, body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AnnotationStash.Add rejected whiteboard annotation");
        }
    }

    private static string BuildAnchorLabel(WhiteboardRegion region)
    {
        // Math.Round to match ToPixelRect — otherwise a 100.6×50.6 region
        // would label as "100x50" while the rendered outline reads 101x51.
        var size = $"{(int)Math.Round(region.Rect.Width)}x{(int)Math.Round(region.Rect.Height)}";
        var leafHint = region.Leaves.Count switch
        {
            0 => string.Empty,
            1 => $" → {region.Leaves[0].Type}",
            _ => $" → {region.Leaves.Count} leaves"
        };
        return $"{region.Kind} gesture ({size}){leafHint}";
    }

    private void OnBadgeCleared(RegionOverlayPair pair)
    {
        if (pair.LastItem is null) return;
        try
        {
            _annotationStash.RemoveItem(pair.LastItem);
            pair.LastItem = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove whiteboard annotation from stash");
        }
    }

    private void OnManualCaptureCompleted() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            // Annotations just shipped — preserve stash entries, drop
            // visuals only.
            TearDown(removeAnnotations: false);
        });

    private void OnCleared() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            // ClearStash hotkey: nuke everything including in-flight notes.
            TearDown(removeAnnotations: true);
        });

    private void TearDown(bool removeAnnotations)
    {
        foreach (var pair in _overlays)
        {
            if (pair.CommittedHandler is not null) pair.Badge.Committed -= pair.CommittedHandler;
            if (pair.ClearedHandler is not null) pair.Badge.Cleared -= pair.ClearedHandler;
            if (removeAnnotations && pair.LastItem is not null)
            {
                try { _annotationStash.RemoveItem(pair.LastItem); }
                catch (Exception ex) { _logger.LogDebug(ex, "TearDown: RemoveItem failed"); }
            }
            pair.LastItem = null;
            pair.Badge.Hide();
            pair.Outline.Hide();
            pair.Outline.Close();
            pair.Badge.Close();
        }
        _overlays.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_drawnHandler is not null)
        {
            _whiteboardStash.Drawn -= _drawnHandler;
            _drawnHandler = null;
        }
        if (_captureCompletedHandler is not null)
        {
            _contextStashWriter.ManualCaptureCompleted -= _captureCompletedHandler;
            _captureCompletedHandler = null;
        }
        if (_clearedHandler is not null)
        {
            _whiteboardStash.Cleared -= _clearedHandler;
            _clearedHandler = null;
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _followTimer?.Stop();
            _followTimer = null;
            TearDown(removeAnnotations: false);
        });
    }

    private sealed class RegionOverlayPair
    {
        public WhiteboardRegion Region { get; }
        public AnnotationOutlineWindow Outline { get; }
        public AnnotationOverlayWindow Badge { get; }
        public AnnotationItem? LastItem { get; set; }
        public EventHandler<string>? CommittedHandler { get; set; }
        public EventHandler? ClearedHandler { get; set; }
        public int RefreshInFlight; // 0 = idle, 1 = refresh in progress

        public RegionOverlayPair(
            WhiteboardRegion region,
            AnnotationOutlineWindow outline,
            AnnotationOverlayWindow badge)
        {
            Region = region;
            Outline = outline;
            Badge = badge;
        }
    }
}
