using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using Everywhere.Views;
using Everywhere.Views.Annotation;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp.Annotation;

/// <summary>
/// Bridges <see cref="PickStash"/> events and the floating annotation
/// overlay. When the user pins an element this surfaces the ➕ badge
/// and an outline rectangle on the element; when the user commits a
/// note the body is forwarded to <see cref="AnnotationStash"/>; when
/// the user fires SnapshotContext (manual capture) we tear the
/// overlay down — the annotation has shipped and the badge no longer
/// belongs on screen.
/// </summary>
public sealed class AnnotationOverlayHost : IAsyncInitializer, IAsyncDisposable
{
    private readonly PickStash _pickStash;
    private readonly AnnotationStash _annotationStash;
    private readonly ContextStashWriter _contextStashWriter;
    private readonly ILogger<AnnotationOverlayHost> _logger;

    private AnnotationOverlayWindow? _overlay;
    private VisualElementOverlayWindow? _outlineOverlay;
    private Action<IVisualElement>? _pinnedHandler;
    private Action? _captureCompletedHandler;
    private EventHandler<string>? _committedHandler;
    private IVisualElement? _currentPinnedElement;

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
                _currentPinnedElement = element;

                // Outline first so the user sees what they just picked.
                _outlineOverlay ??= new VisualElementOverlayWindow();
                _outlineOverlay.UpdateForVisualElement(element);

                if (_overlay is null)
                {
                    _overlay = new AnnotationOverlayWindow();
                    _committedHandler = OnCommitted;
                    _overlay.Committed += _committedHandler;
                }
                _overlay.ShowFor(element);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to show annotation overlay");
            }
        });
    }

    private void OnManualCaptureCompleted()
    {
        // The user just pressed SnapshotContext, the agent app raised,
        // the annotations queued for the next send have shipped. Hide
        // both overlays so a stale badge doesn't keep floating over a
        // window the user is no longer pointed at.
        Dispatcher.UIThread.Post(() =>
        {
            _overlay?.Hide();
            _outlineOverlay?.UpdateForVisualElement(null);
            _currentPinnedElement = null;
        });
    }

    private void OnCommitted(object? sender, string body)
    {
        var element = _currentPinnedElement;
        if (element is null)
        {
            _logger.LogWarning("Annotation committed but no pinned element captured; dropping");
            return;
        }

        string anchorLabel;
        try
        {
            var name = element.Name;
            var type = element.Type.ToString();
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
            if (_overlay is not null && _committedHandler is not null)
            {
                _overlay.Committed -= _committedHandler;
                _committedHandler = null;
            }
            _overlay?.Close();
            _overlay = null;
            _outlineOverlay?.Close();
            _outlineOverlay = null;
        });
    }
}
