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

    public async Task InitializeAsync()
    {
        ApplySettingsToOptions();
        settings.McpServer.PropertyChanged += async (_, _) => await RestartIfNeededAsync();

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

    private async Task RestartIfNeededAsync()
    {
        try
        {
            await host.StopAsync(CancellationToken.None);
            ApplySettingsToOptions();
            if (settings.McpServer.HttpEnabled)
            {
                await host.StartAsync(CancellationToken.None);
                logger.LogInformation("MCP HTTP transport restarted on port {Port}.", host.BoundPort);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to restart MCP HTTP transport.");
        }
    }
}
