using Everywhere.Common;
using Everywhere.Configuration;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp.OpenDia;

/// <summary>
/// Boots the opendia websocket bridge based on user settings, and reacts to
/// runtime toggles (enable/disable, port change) without requiring an app
/// restart. The bridge itself is a singleton so other Everywhere services
/// (MCP tool factory, status panels) can read its connection state.
/// </summary>
public sealed class OpenDiaBridgeInitializer : IAsyncInitializer, IAsyncDisposable
{
    private readonly OpenDiaBridge _bridge;
    private readonly Settings _settings;
    private readonly ILogger<OpenDiaBridgeInitializer> _logger;
    // Rapid toggles from the settings UI fire PropertyChanged on the UI
    // thread; serialise the apply work so a port-change race can't leave
    // two listeners alive on the old port.
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private CancellationTokenSource? _runCts;

    public OpenDiaBridgeInitializer(
        OpenDiaBridge bridge,
        Settings settings,
        ILogger<OpenDiaBridgeInitializer> logger)
    {
        _bridge = bridge;
        _settings = settings;
        _logger = logger;
    }

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    public async Task InitializeAsync()
    {
        await ApplyCurrent().ConfigureAwait(false);
        _settings.McpServer.PropertyChanged += async (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(McpServerSettings.OpenDiaEnabled):
                case nameof(McpServerSettings.OpenDiaPort):
                    try { await ApplyCurrent().ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "OpenDia: settings change handler failed");
                    }
                    break;
            }
        };
    }

    private async Task ApplyCurrent()
    {
        await _applyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Always cancel + dispose the previous CTS before binding new
            // — otherwise repeated toggles leak CTS instances and old
            // listener threads.
            var oldCts = _runCts;
            _runCts = null;
            try { oldCts?.Cancel(); } catch { /* ignore */ }
            try { oldCts?.Dispose(); } catch { /* ignore */ }
            await _bridge.StopAsync().ConfigureAwait(false);

            if (!_settings.McpServer.OpenDiaEnabled) return;
            var port = _settings.McpServer.OpenDiaPort;
            if (port < 1 || port > 65535)
            {
                _logger.LogWarning("OpenDia: invalid port {Port}, ignoring.", port);
                return;
            }
            var cts = new CancellationTokenSource();
            _runCts = cts;
            await _bridge.StartAsync(port, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenDia: failed to apply settings");
        }
        finally
        {
            _applyLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _bridge.StopAsync().ConfigureAwait(false);
        }
        catch { /* ignore on shutdown */ }
        try { _runCts?.Cancel(); } catch { /* ignore */ }
        try { _runCts?.Dispose(); } catch { /* ignore */ }
        _applyLock.Dispose();
    }
}
