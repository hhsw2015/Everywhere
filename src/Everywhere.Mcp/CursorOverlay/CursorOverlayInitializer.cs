using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Mcp.Input;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp.CursorOverlay;

/// <summary>
/// Owns the SoftwareCursorOverlay singleton's lifecycle. Watches
/// settings (CursorOverlayEnabled toggle) and brings the overlay up or
/// down accordingly. Subscribes the bridge so input-simulator trace
/// events flow into the overlay only while it is alive.
/// </summary>
public sealed class CursorOverlayInitializer : IAsyncInitializer, ITargetWindowHighlighter, IAsyncDisposable
{
    private readonly Settings _settings;
    private readonly CursorTrace _trace;
    private readonly IWindowHelper? _windowHelper;
    private readonly ILogger<CursorOverlayInitializer> _logger;
    private SoftwareCursorOverlay? _overlay;
    private CursorOverlayBridge? _bridge;
    private TargetWindowIndicator? _indicator;

    public CursorOverlayInitializer(
        Settings settings,
        CursorTrace trace,
        ILogger<CursorOverlayInitializer> logger,
        IWindowHelper? windowHelper = null)
    {
        _settings = settings;
        _trace = trace;
        _logger = logger;
        // Mac path provides IWindowHelper which lets us set
        // NSWindow.IgnoresMouseEvents on the indicator overlay. Without
        // it, IsHitTestVisible=false only blocks Avalonia hit-testing —
        // the underlying NSWindow still receives mouse events and would
        // steal focus from the target app the indicator is trying to
        // frame. Optional so non-Mac hosts work degraded.
        _windowHelper = windowHelper;
    }

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    public Task InitializeAsync()
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
                        _indicator = new TargetWindowIndicator(_windowHelper);
                        _logger.LogInformation("CursorOverlay: bridge + indicator attached.");
                    }
                }
                else
                {
                    var bridge = _bridge; _bridge = null;
                    var overlay = _overlay; _overlay = null;
                    var ind = _indicator; _indicator = null;
                    bridge?.Dispose();
                    if (ind is not null) _ = ind.DisposeAsync();
                    if (overlay is not null) _ = overlay.DisposeAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CursorOverlay: failed to apply state");
            }
        });
    }

    public void Highlight(Avalonia.PixelRect rect, string? label = null)
    {
        var ind = _indicator;
        if (ind is null) { _logger.LogDebug("CursorOverlay: Highlight skipped (no indicator)"); return; }
        _logger.LogInformation(
            "CursorOverlay: Highlight rect={X},{Y} {W}x{H} label={Label}",
            rect.X, rect.Y, rect.Width, rect.Height, label);
        // Always marshal to UI thread. Tools call us from MCP worker
        // threads; touching an Avalonia Window from a non-UI thread
        // throws (which previously bubbled all the way out of the tool
        // call). Swallow any UI-side failure so the indicator stays
        // strictly best-effort: a missing border must never break a
        // click / type / press.
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try { ind.ShowFor(rect, label ?? "🤖 Everywhere operating"); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "CursorOverlay: indicator ShowFor failed");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CursorOverlay: indicator post failed");
        }
    }

    public ValueTask DisposeAsync()
    {
        _bridge?.Dispose(); _bridge = null;
        var ind = _indicator; _indicator = null;
        if (ind is not null) _ = ind.DisposeAsync();
        if (_overlay is not null) { var o = _overlay; _overlay = null; return o.DisposeAsync(); }
        return ValueTask.CompletedTask;
    }
}
