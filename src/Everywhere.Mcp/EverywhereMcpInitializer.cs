using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Mcp.Snapshot;
using Everywhere.Mcp.Transport;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp;

/// <summary>
/// Boots the in-process MCP HTTP listener (loopback only) once the GUI services are up.
/// Reads its port + enabled flag from <see cref="McpServerSettings"/> and reacts to changes
/// at runtime by stop/start cycling the listener.
/// </summary>
public sealed class EverywhereMcpInitializer : IAsyncInitializer
{
    private readonly Settings _settings;
    private readonly EverywhereMcpHttpOptions _options;
    private readonly EverywhereMcpHttpHost _host;
    private readonly ILogger<EverywhereMcpInitializer> _logger;

    public EverywhereMcpInitializer(
        Settings settings,
        EverywhereMcpHttpOptions options,
        EverywhereMcpHttpHost host,
        SelectionCache selectionCache,
        ILogger<EverywhereMcpInitializer> logger)
    {
        _settings = settings;
        _options = options;
        _host = host;
        _logger = logger;
        // Resolving SelectionCache via DI here is the side-effect we want — its ctor
        // subscribes to the platform's text-selection stream from process start.
        _ = selectionCache;
    }

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    private readonly SemaphoreSlim _restartLock = new(1, 1);
    private CancellationTokenSource? _pendingDebounce;

    public async Task InitializeAsync()
    {
        ApplySettingsToOptions();
        _settings.McpServer.PropertyChanged += (_, _) => ScheduleRestart();

        if (!_settings.McpServer.HttpEnabled)
        {
            _logger.LogInformation("MCP HTTP transport disabled by user; stdio-only mode.");
            return;
        }

        try
        {
            await _host.StartAsync(CancellationToken.None);
            _logger.LogInformation("MCP HTTP transport listening on port {Port}.", _host.BoundPort);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start MCP HTTP transport.");
        }
    }

    private void ApplySettingsToOptions()
    {
        try
        {
            _options.Port = _settings.McpServer.HttpPort;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            _logger.LogWarning(ex, "Invalid MCP port {Port}; falling back to default.", _settings.McpServer.HttpPort);
        }
        _options.Enabled = _settings.McpServer.HttpEnabled;
    }

    /// <summary>
    /// Coalesce a burst of property-change notifications (the user typing into the port
    /// box, toggling enabled, etc.) into a single restart. Without this, two property
    /// changes in quick succession both call StopAsync→StartAsync; the second StartAsync
    /// can race past the first's StopAsync, leaving a Kestrel listener bound that nobody
    /// holds a reference to (port leak + zombie listener).
    /// </summary>
    private void ScheduleRestart()
    {
        var oldCts = Interlocked.Exchange(ref _pendingDebounce, new CancellationTokenSource());
        oldCts?.Cancel();
        oldCts?.Dispose();

        var token = _pendingDebounce!.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await RestartAsync(token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MCP HTTP restart failed.");
            }
        });
    }

    private async Task RestartAsync(CancellationToken token)
    {
        await _restartLock.WaitAsync(token);
        try
        {
            await _host.StopAsync(CancellationToken.None);
            ApplySettingsToOptions();

            if (!_settings.McpServer.HttpEnabled)
            {
                _logger.LogInformation("MCP HTTP transport disabled by user.");
                return;
            }

            await _host.StartAsync(CancellationToken.None);
            _logger.LogInformation("MCP HTTP transport restarted on port {Port}.", _host.BoundPort);
        }
        finally
        {
            _restartLock.Release();
        }
    }
}
