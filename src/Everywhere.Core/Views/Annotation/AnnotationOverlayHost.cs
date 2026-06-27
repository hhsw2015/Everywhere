using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Interop;
using Microsoft.Extensions.Logging;

namespace Everywhere.Views.Annotation;

/// <summary>
/// Bridges <see cref="PickStash"/> events and the floating annotation
/// overlay (v0.9.170). When the user pins an element, this surfaces
/// the ➕ badge next to it; when the user commits a note in the
/// popover, the note is forwarded to <see cref="AnnotationStash"/>
/// with the just-pinned element as its anchor.
///
/// Single-pin only for now — multi-select pinning is a follow-up
/// (PickStash itself is still single-slot at v0.9.170).
/// </summary>
public sealed class AnnotationOverlayHost : IAsyncInitializer, IAsyncDisposable
{
    private readonly PickStash _pickStash;
    private readonly AnnotationStash _annotationStash;
    private readonly ILogger<AnnotationOverlayHost> _logger;

    private AnnotationOverlayWindow? _overlay;
    private Action<IVisualElement>? _pinnedHandler;
    private EventHandler<string>? _committedHandler;
    private IVisualElement? _currentPinnedElement;

    public AnnotationOverlayHost(
        PickStash pickStash,
        AnnotationStash annotationStash,
        ILogger<AnnotationOverlayHost> logger)
    {
        _pickStash = pickStash;
        _annotationStash = annotationStash;
        _logger = logger;
    }

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    public Task InitializeAsync()
    {
        _pinnedHandler = OnPinned;
        _pickStash.Pinned += _pinnedHandler;
        _logger.LogInformation("AnnotationOverlayHost subscribed to PickStash.Pinned");
        return Task.CompletedTask;
    }

    private void OnPinned(IVisualElement element)
    {
        _logger.LogInformation("AnnotationOverlayHost.OnPinned: element received, dispatching to UI thread");
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _currentPinnedElement = element;
                if (_overlay is null)
                {
                    _overlay = new AnnotationOverlayWindow();
                    _committedHandler = OnCommitted;
                    _overlay.Committed += _committedHandler;
                }
                _overlay.ShowFor(element);
                _logger.LogInformation("AnnotationOverlayHost: ShowFor invoked");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to show annotation overlay");
            }
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

        // Compose an anchor_label from whatever the element can tell us.
        // BoundingRectangle is on the UI thread; Name/Type are cheap.
        // Anything missing degrades the label but doesn't fail the commit.
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
            // Length / queue depth cap. Surface to logs; the popover
            // doesn't currently have an inline error path.
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

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_overlay is not null && _committedHandler is not null)
            {
                _overlay.Committed -= _committedHandler;
                _committedHandler = null;
            }
            _overlay?.Close();
            _overlay = null;
        });
    }
}
