using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Interop;
using Microsoft.Extensions.Logging;

namespace Everywhere.Views.Annotation;

/// <summary>
/// Spike host (v0.9.167): subscribes to <see cref="PickStash.Pinned"/>
/// and shows an <see cref="AnnotationOverlayWindow"/> next to the
/// just-pinned element. No textarea yet — visual position validation
/// only. Once we're happy with placement we add ➕ → textarea →
/// AnnotationStash.Add in this same class.
/// </summary>
public sealed class AnnotationOverlayHost : IAsyncInitializer, IAsyncDisposable
{
    private readonly PickStash _pickStash;
    private readonly ILogger<AnnotationOverlayHost> _logger;
    private AnnotationOverlayWindow? _overlay;
    private Action<IVisualElement>? _handler;

    public AnnotationOverlayHost(PickStash pickStash, ILogger<AnnotationOverlayHost> logger)
    {
        _pickStash = pickStash;
        _logger = logger;
    }

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    public Task InitializeAsync()
    {
        _handler = OnPinned;
        _pickStash.Pinned += _handler;
        _logger.LogInformation("AnnotationOverlayHost subscribed to PickStash.Pinned");
        return Task.CompletedTask;
    }

    private void OnPinned(IVisualElement element)
    {
        // Log at Information so we can see it in the user-facing log
        // without a debug filter. We can drop back to Debug once the
        // overlay UI is finalised.
        _logger.LogInformation("AnnotationOverlayHost.OnPinned: element received, dispatching to UI thread");
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _overlay ??= new AnnotationOverlayWindow();
                _overlay.ShowFor(element);
                _logger.LogInformation("AnnotationOverlayHost: ShowFor invoked");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to show annotation overlay");
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_handler is not null)
        {
            _pickStash.Pinned -= _handler;
            _handler = null;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _overlay?.Close();
            _overlay = null;
        });
    }
}
