using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Server;

/// <summary>
/// Boots the Everywhere MCP server over stdio. Used by the <c>--mcp</c> CLI entrypoint.
/// HTTP transport is registered separately by <c>AddEverywhereMcp()</c> against the
/// already-running GUI Kestrel host (see <c>EverywhereMcpServiceExtensions</c>).
/// </summary>
public static class EverywhereMcpServer
{
    public static async Task RunStdioAsync(
        string[] args,
        Action<IServiceCollection>? configure = null,
        CancellationToken cancellationToken = default)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.AddConsole(options =>
        {
            // stdout is reserved for the MCP frame channel; logs must go to stderr.
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        // Platform projects can register the real IVisualElementContext / IInputSimulator /
        // IFocusBackend BEFORE AddEverywhereMcpTools() runs its TryAdd fallbacks, so the
        // stdio transport gets full a11y access without spinning up Avalonia.
        configure?.Invoke(builder.Services);

        builder.Services
            .AddEverywhereMcpTools()
            .AddMcpServer(options =>
            {
                options.ServerInfo = new()
                {
                    Name = "everywhere",
                    Version = typeof(EverywhereMcpServer).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                };
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(EverywhereMcpServer).Assembly);

        await builder.Build().RunAsync(cancellationToken);
    }
}
