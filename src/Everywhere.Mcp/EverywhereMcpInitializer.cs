using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Mcp.Transport;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp;

/// <summary>
/// Boots the in-process MCP HTTP listener (loopback only) once the GUI services are up.
/// Reads its port + enabled flag from <see cref="McpServerSettings"/> and reacts to changes
/// at runtime by stop/start cycling the listener.
/// </summary>
public sealed class EverywhereMcpInitializer(
    Settings settings,
    EverywhereMcpHttpOptions options,
    EverywhereMcpHttpHost host,
    ILogger<EverywhereMcpInitializer> logger
) : IAsyncInitializer
{
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    private readonly SemaphoreSlim _restartLock = new(1, 1);
    private CancellationTokenSource? _pendingDebounce;

    public async Task InitializeAsync()
    {
        ApplySettingsToOptions();
        settings.McpServer.PropertyChanged += (_, _) => ScheduleRestart();

        if (!settings.McpServer.HttpEnabled)
        {
            logger.LogInformation("MCP HTTP transport disabled by user; stdio-only mode.");
            return;
        }

        try
        {
            await host.StartAsync(CancellationToken.None);
            logger.LogInformation("MCP HTTP transport listening on port {Port}.", host.BoundPort);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start MCP HTTP transport.");
        }
    }

    private void ApplySettingsToOptions()
    {
        try
        {
            options.Port = settings.McpServer.HttpPort;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            logger.LogWarning(ex, "Invalid MCP port {Port}; falling back to default.", settings.McpServer.HttpPort);
        }
        options.Enabled = settings.McpServer.HttpEnabled;
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
                logger.LogError(ex, "MCP HTTP restart failed.");
            }
        });
    }

    private async Task RestartAsync(CancellationToken token)
    {
        await _restartLock.WaitAsync(token);
        try
        {
            await host.StopAsync(CancellationToken.None);
            ApplySettingsToOptions();

            if (!settings.McpServer.HttpEnabled)
            {
                logger.LogInformation("MCP HTTP transport disabled by user.");
                return;
            }

            await host.StartAsync(CancellationToken.None);
            logger.LogInformation("MCP HTTP transport restarted on port {Port}.", host.BoundPort);
        }
        finally
        {
            _restartLock.Release();
        }
    }
}
