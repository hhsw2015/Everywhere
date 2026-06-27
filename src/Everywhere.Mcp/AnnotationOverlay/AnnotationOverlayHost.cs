using Avalonia;
using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using Everywhere.Views.Annotation;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp.AnnotationOverlay;

/// <summary>
/// Bridges <see cref="PickStash"/> events and the floating annotation
/// overlays. Each pin gets its own ➕ badge + outline pair so the user
/// can see WHERE every annotation lives. On SnapshotContext the whole
/// fleet is torn down — the annotations have shipped to the agent and
/// the badges no longer belong on screen.
///
/// Anchor is FIXED at pin time (screen-coordinate snapshot, Figma-comment
/// style). No follow-up timer — AX BoundingRectangle on Chromium-based
/// hosts (Arc, Brave) returns stale or 0×0 bounds during scroll, which
/// produced "✓ in wrong place" reports. A static screen coordinate is
/// less fancy but always correct: when the user scrolls the element away
/// the ✓ stays put — the visual reminder "you have a note in this region"
/// outranks tracking the now-off-screen element.
/// </summary>
public sealed class AnnotationOverlayHost : IAsyncInitializer, IAsyncDisposable
{
    private readonly PickStash _pickStash;
    private readonly AnnotationStash _annotationStash;
    private readonly ContextStashWriter _contextStashWriter;
    private readonly IWindowHelper _windowHelper;
    private readonly ILogger<AnnotationOverlayHost> _logger;

    private readonly List<PinOverlayPair> _overlays = new();
    private Action<IVisualElement>? _pinnedHandler;
    private Action? _captureCompletedHandler;
    private volatile bool _disposed;

    public AnnotationOverlayHost(
        PickStash pickStash,
        AnnotationStash annotationStash,
        ContextStashWriter contextStashWriter,
        IWindowHelper windowHelper,
        ILogger<AnnotationOverlayHost> logger)
    {
        _pickStash = pickStash;
        _annotationStash = annotationStash;
        _contextStashWriter = contextStashWriter;
        _windowHelper = windowHelper;
        _logger = logger;
    }

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    public Task InitializeAsync()
    {
        _pinnedHandler = OnPinned;
        _pickStash.Pinned += _pinnedHandler;

        _captureCompletedHandler = OnManualCaptureCompleted;
        _contextStashWriter.ManualCaptureCompleted += _captureCompletedHandler;

        _logger.LogInformation("AnnotationOverlayHost subscribed (Pinned + ManualCaptureCompleted)");
        return Task.CompletedTask;
    }

    private void OnPinned(IVisualElement element)
    {
        // Resolve AX bounds OFF the UI thread, then jump onto it to create
        // the windows. One resolve covers both outline and badge so they
        // can't disagree about the anchor rect, and there's no concurrent
        // AX query racing with itself.
        _ = Task.Run(async () =>
        {
            PixelRect rect;
            try
            {
                rect = await Task.Run(() => element.BoundingRectangle).WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve AX bounds for annotation overlay");
                return;
            }

            if (rect.Width <= 0 || rect.Height <= 0)
            {
                _logger.LogDebug("Pinned element has empty AX bounds; skipping overlay");
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed) return;
                try
                {
                    var outline = new AnnotationOutlineWindow();
                    outline.ShowAt(rect);
                    // Configure AFTER Show so the NSWindow exists and the
                    // native-level apply runs synchronously instead of via
                    // deferred Opened. Avoids a 1-frame default-level flash.
                    _windowHelper.ConfigureAsCursorOverlay(outline);

                    var badge = new AnnotationOverlayWindow();
                    var pair = new PinOverlayPair(element, outline, badge);
                    pair.CommittedHandler = (_, body) => OnCommitted(pair, body);
                    pair.ClearedHandler = (_, _) => OnCleared(pair);
                    badge.Committed += pair.CommittedHandler;
                    badge.Cleared += pair.ClearedHandler;
                    _overlays.Add(pair);

                    badge.ShowAt(rect);
                    _windowHelper.ConfigureAsInteractiveOverlay(badge);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to show annotation overlay");
                }
            });
        });
    }

    private void OnManualCaptureCompleted()
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var pair in _overlays)
            {
                // Unsubscribe BEFORE close — otherwise Avalonia's teardown
                // can fire one last Committed/Cleared which would remove
                // the annotation we just shipped to the agent.
                if (pair.CommittedHandler is not null) pair.Badge.Committed -= pair.CommittedHandler;
                if (pair.ClearedHandler is not null) pair.Badge.Cleared -= pair.ClearedHandler;
                pair.LastItem = null;
                pair.Badge.Hide();
                pair.Outline.Hide();
                pair.Outline.Close();
                pair.Badge.Close();
            }
            _overlays.Clear();
        });
    }

    private void OnCommitted(PinOverlayPair pair, string body)
    {
        string anchorLabel;
        try
        {
            var name = pair.Element.Name;
            var type = pair.Element.Type.ToString();
            anchorLabel = string.IsNullOrWhiteSpace(name)
                ? type
                : $"{type} \"{name}\"";
        }
        catch
        {
            anchorLabel = "pinned element";
        }

        // Re-edit: drop the previous stash entry from this pair so we
        // don't accumulate revisions in the payload. Done in its own
        // try/catch so a failed remove doesn't skip the Add below — if
        // we couldn't drop the old one, the new one still needs to go
        // in or the user's commit silently vanishes.
        if (pair.LastItem is not null)
        {
            try
            {
                _annotationStash.RemoveItem(pair.LastItem);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AnnotationStash.RemoveItem failed; proceeding with Add");
            }
        }

        try
        {
            var item = new AnnotationItem(
                Source: AnnotationSource.Pin,
                Body: body,
                AnchorRef: null,
                AnchorLabel: anchorLabel,
                CapturedAtUtc: DateTimeOffset.UtcNow);
            _annotationStash.Add(item);
            pair.LastItem = item;
            _logger.LogInformation("Annotation committed for anchor {Anchor}: {Body}", anchorLabel, body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AnnotationStash.Add rejected annotation");
        }
    }

    private void OnCleared(PinOverlayPair pair)
    {
        if (pair.LastItem is null) return;
        try
        {
            _annotationStash.RemoveItem(pair.LastItem);
            pair.LastItem = null;
            _logger.LogInformation("Annotation cleared for pair");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove annotation from stash");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_pinnedHandler is not null)
        {
            _pickStash.Pinned -= _pinnedHandler;
            _pinnedHandler = null;
        }
        if (_captureCompletedHandler is not null)
        {
            _contextStashWriter.ManualCaptureCompleted -= _captureCompletedHandler;
            _captureCompletedHandler = null;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var pair in _overlays)
            {
                pair.Badge.Close();
                pair.Outline.Close();
            }
            _overlays.Clear();
        });
    }

    private sealed class PinOverlayPair
    {
        public IVisualElement Element { get; }
        public AnnotationOutlineWindow Outline { get; }
        public AnnotationOverlayWindow Badge { get; }
        public AnnotationItem? LastItem { get; set; }
        public EventHandler<string>? CommittedHandler { get; set; }
        public EventHandler? ClearedHandler { get; set; }

        public PinOverlayPair(
            IVisualElement element,
            AnnotationOutlineWindow outline,
            AnnotationOverlayWindow badge)
        {
            Element = element;
            Outline = outline;
            Badge = badge;
        }
    }
}
