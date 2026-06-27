using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using Everywhere.Views;
using Everywhere.Views.Annotation;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp.AnnotationOverlay;

/// <summary>
/// Bridges <see cref="PickStash"/> events and the floating annotation
/// overlays. Each pin gets its own ➕ badge + outline pair so the user
/// can see WHERE every annotation lives (per "我得知道我是在哪里标注的").
/// On SnapshotContext the whole fleet is torn down — the annotations
/// have shipped to the agent and the badges no longer belong on screen.
/// </summary>
public sealed class AnnotationOverlayHost : IAsyncInitializer, IAsyncDisposable
{
    private readonly PickStash _pickStash;
    private readonly AnnotationStash _annotationStash;
    private readonly ContextStashWriter _contextStashWriter;
    private readonly ILogger<AnnotationOverlayHost> _logger;

    private readonly List<PinOverlayPair> _overlays = new();
    private Action<IVisualElement>? _pinnedHandler;
    private Action? _captureCompletedHandler;

    public AnnotationOverlayHost(
        PickStash pickStash,
        AnnotationStash annotationStash,
        ContextStashWriter contextStashWriter,
        ILogger<AnnotationOverlayHost> logger)
    {
        _pickStash = pickStash;
        _annotationStash = annotationStash;
        _contextStashWriter = contextStashWriter;
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
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var outline = new VisualElementOverlayWindow();
                outline.UpdateForVisualElement(element);

                var badge = new AnnotationOverlayWindow();
                var pair = new PinOverlayPair(element, outline, badge);
                badge.Committed += (_, body) => OnCommitted(pair, body);
                _overlays.Add(pair);

                badge.ShowFor(element);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to show annotation overlay");
            }
        });
    }

    private void OnManualCaptureCompleted()
    {
        // SnapshotContext fired → annotations shipped → tear down the
        // fleet so stale badges don't float over windows the user has
        // moved past.
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var pair in _overlays)
            {
                pair.Badge.Hide();
                pair.Outline.UpdateForVisualElement(null);
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

        try
        {
            _annotationStash.Add(new AnnotationItem(
                Source: AnnotationSource.Pin,
                Body: body,
                AnchorRef: null,
                AnchorLabel: anchorLabel,
                CapturedAtUtc: DateTimeOffset.UtcNow));
            _logger.LogInformation("Annotation committed for anchor {Anchor}: {Body}", anchorLabel, body);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "AnnotationStash rejected annotation");
        }
    }

    public async ValueTask DisposeAsync()
    {
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

    private sealed record PinOverlayPair(
        IVisualElement Element,
        VisualElementOverlayWindow Outline,
        AnnotationOverlayWindow Badge);
}
