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
public sealed class OpenDiaBridgeInitializer : IAsyncInitializer
{
    private readonly OpenDiaBridge _bridge;
    private readonly Settings _settings;
    private readonly ILogger<OpenDiaBridgeInitializer> _logger;
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
        ApplyCurrent();
        _settings.McpServer.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(McpServerSettings.OpenDiaEnabled):
                case nameof(McpServerSettings.OpenDiaPort):
                    ApplyCurrent();
                    break;
            }
        };
        await Task.CompletedTask;
    }

    private void ApplyCurrent()
    {
        try
        {
            // Synchronously stop + release the old listener BEFORE binding
            // a new one — HttpListener.Start() throws if the port is still
            // claimed, even by us. Also clears state-change subscribers
            // (e.g. tool sync) so they reconcile to "disconnected".
            _runCts?.Cancel();
            _runCts = null;
            _bridge.Stop();

            if (!_settings.McpServer.OpenDiaEnabled) return;
            var port = _settings.McpServer.OpenDiaPort;
            if (port < 1 || port > 65535)
            {
                _logger.LogWarning("OpenDia: invalid port {Port}, ignoring.", port);
                return;
            }
            _runCts = new CancellationTokenSource();
            _ = _bridge.StartAsync(port, _runCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenDia: failed to apply settings");
        }
    }
}
