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
/// Outline + badge follow the element. A 150ms DispatcherTimer re-queries
/// each pair's BoundingRectangle and slides Outline/Badge to the new
/// screen coords; if the rect goes empty (element scrolled out of view,
/// host window hidden) both hide and reappear when bounds are good again.
/// User feedback rejected the static-screen-coord variant: "之前标注的框
/// 停在原处, 视觉上看起来变成框选下方新元素, 语义不对".
/// The badge skips MoveTo while expanded so we don't yank the popover
/// under a typing user.
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
    private Action? _pickStashClearedHandler;
    private DispatcherTimer? _followTimer;
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

        _pickStashClearedHandler = OnPickStashCleared;
        _pickStash.Cleared += _pickStashClearedHandler;

        Dispatcher.UIThread.Post(() =>
        {
            _followTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _followTimer.Tick += OnFollowTick;
            _followTimer.Start();
        });

        _logger.LogInformation("AnnotationOverlayHost subscribed");
        return Task.CompletedTask;
    }

    private void OnPickStashCleared()
    {
        // User pressed ClearContextStash hotkey. Wipe outlines + badges
        // and drop any in-flight stash entries so the next prompt is
        // genuinely clean.
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var pair in _overlays)
            {
                if (pair.CommittedHandler is not null) pair.Badge.Committed -= pair.CommittedHandler;
                if (pair.ClearedHandler is not null) pair.Badge.Cleared -= pair.ClearedHandler;
                if (pair.LastItem is not null)
                {
                    try { _annotationStash.RemoveItem(pair.LastItem); }
                    catch (Exception ex) { _logger.LogDebug(ex, "ClearStash: RemoveItem failed"); }
                }
                pair.LastItem = null;
                pair.Badge.Hide();
                pair.Outline.Hide();
                pair.Outline.Close();
                pair.Badge.Close();
            }
            _overlays.Clear();
        });
    }

    private void OnFollowTick(object? sender, EventArgs e)
    {
        if (_overlays.Count == 0) return;
        foreach (var pair in _overlays)
        {
            // Single shared rect query so outline + badge can't disagree.
            _ = RefreshPairAsync(pair);
        }
    }

    private async Task RefreshPairAsync(PinOverlayPair pair)
    {
        if (Interlocked.CompareExchange(ref pair.RefreshInFlight, 1, 0) != 0) return;
        try
        {
            PixelRect rect;
            try
            {
                // Live variant skips per-element cache so this reflects
                // the current screen position — the cached BoundingRectangle
                // froze at pin time and the badge never moved.
                rect = await Task.Run(() => pair.Element.BoundingRectangleLive).WaitAsync(TimeSpan.FromMilliseconds(150));
            }
            catch
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed) return;
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
            _logger.LogDebug(ex, "RefreshPair failed");
        }
        finally
        {
            Interlocked.Exchange(ref pair.RefreshInFlight, 0);
        }
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
        if (_pickStashClearedHandler is not null)
        {
            _pickStash.Cleared -= _pickStashClearedHandler;
            _pickStashClearedHandler = null;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _followTimer?.Stop();
            _followTimer = null;
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
        public int RefreshInFlight; // 0 = idle, 1 = refresh in progress

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
