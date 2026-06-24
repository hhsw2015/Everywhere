using Avalonia.Threading;
using Everywhere.Configuration;
using Everywhere.Initialization;
using Everywhere.Mcp.Input;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp.CursorOverlay;

/// <summary>
/// Owns the SoftwareCursorOverlay singleton's lifecycle. Watches
/// settings (CursorOverlayEnabled toggle) and brings the overlay up or
/// down accordingly. Subscribes the bridge so input-simulator trace
/// events flow into the overlay only while it is alive.
/// </summary>
public sealed class CursorOverlayInitializer : IAsyncInitializer, IAsyncDisposable
{
    private readonly Settings _settings;
    private readonly CursorTrace _trace;
    private readonly ILogger<CursorOverlayInitializer> _logger;
    private SoftwareCursorOverlay? _overlay;
    private CursorOverlayBridge? _bridge;

    public CursorOverlayInitializer(Settings settings, CursorTrace trace, ILogger<CursorOverlayInitializer> logger)
    {
        _settings = settings;
        _trace = trace;
        _logger = logger;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _settings.McpServer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_settings.McpServer.CursorOverlayEnabled))
                ApplyState();
        };
        ApplyState();
        return Task.CompletedTask;
    }

    private void ApplyState()
    {
        var enabled = _settings.McpServer.CursorOverlayEnabled;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (enabled)
                {
                    if (_overlay is null)
                    {
                        _overlay = new SoftwareCursorOverlay();
                        _bridge = new CursorOverlayBridge(_trace, _overlay);
                        _logger.LogInformation("CursorOverlay: bridge attached to input trace channel.");
                    }
                }
                else
                {
                    var bridge = _bridge; _bridge = null;
                    var overlay = _overlay; _overlay = null;
                    bridge?.Dispose();
                    if (overlay is not null) _ = overlay.DisposeAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CursorOverlay: failed to apply state");
            }
        });
    }

    public ValueTask DisposeAsync()
    {
        _bridge?.Dispose(); _bridge = null;
        if (_overlay is not null) { var o = _overlay; _overlay = null; return o.DisposeAsync(); }
        return ValueTask.CompletedTask;
    }
}
