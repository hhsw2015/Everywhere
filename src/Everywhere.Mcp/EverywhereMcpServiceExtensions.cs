using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp;

/// <summary>
/// DI entrypoints for the Everywhere MCP server.
/// </summary>
public static class EverywhereMcpServiceExtensions
{
    /// <summary>
    /// Registers Everywhere MCP tool services into the GUI host. Use together with
    /// <see cref="AddEverywhereMcpHttpTransport"/> to expose them over Kestrel.
    /// </summary>
    public static IServiceCollection AddEverywhereMcp(this IServiceCollection services)
    {
        services.AddEverywhereMcpTools();

        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new()
                {
                    Name = "everywhere",
                    Version = typeof(EverywhereMcpServiceExtensions).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                };
            })
            .WithHttpTransport(options => options.Stateless = true)
            .WithToolsFromAssembly(typeof(EverywhereMcpServiceExtensions).Assembly);

        return services;
    }

    internal static IServiceCollection AddEverywhereMcpTools(this IServiceCollection services)
    {
        services.TryAddSingleton<SessionStore>();
        // Stdio fallback: GUI host pre-registers a real IVisualElementContext, so
        // TryAdd never overrides it.
        services.TryAddSingleton<IVisualElementContext, EmptyVisualElementContext>();
        return services;
    }
}
