using Everywhere.Interop;
using Everywhere.Mcp.Input;
using Everywhere.Mcp.Snapshot;
using Everywhere.Mcp.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Everywhere.Mcp;

/// <summary>
/// DI entrypoints for the Everywhere MCP server.
/// </summary>
public static class EverywhereMcpServiceExtensions
{
    /// <summary>
    /// Registers Everywhere MCP tool services + the in-process Kestrel listener.
    /// Call from your GUI host's <c>Program.cs</c> after registering platform-specific
    /// <see cref="IInputSimulator"/> / <see cref="IFocusBackend"/> bindings.
    /// </summary>
    public static IServiceCollection AddEverywhereMcp(
        this IServiceCollection services,
        Action<EverywhereMcpHttpOptions>? configure = null)
    {
        services.AddEverywhereMcpTools();
        services.TryAddSingleton(sp =>
        {
            var options = new EverywhereMcpHttpOptions();
            configure?.Invoke(options);
            return options;
        });
        services.AddSingleton<EverywhereMcpHttpHost>();
        // Avalonia GUI hosts don't run a generic-host pipeline, so expose the listener as
        // an explicit Start call instead of an IHostedService. Hosts that *do* run a
        // generic host can register the host as IHostedService themselves.
        return services;
    }

    /// <summary>
    /// Boots the in-process Kestrel listener for the streamable-HTTP transport. Call this
    /// from the GUI's startup sequence after <see cref="AddEverywhereMcp"/> and after the
    /// platform <see cref="IInputSimulator"/> is registered.
    /// </summary>
    public static async Task StartEverywhereMcpHttpAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var host = services.GetRequiredService<EverywhereMcpHttpHost>();
        await host.StartAsync(cancellationToken);
    }

    internal static IServiceCollection AddEverywhereMcpTools(this IServiceCollection services)
    {
        services.TryAddSingleton<SessionStore>();
        services.TryAddSingleton<PickStash>();
        services.TryAddSingleton<IVisualElementContext, EmptyVisualElementContext>();
        services.TryAddSingleton<IInputSimulator, NotSupportedInputSimulator>();
        services.TryAddSingleton<IFocusBackend, NotSupportedFocusBackend>();
        services.TryAddSingleton<FocusBorrow>();
        services.TryAddSingleton<SelectionCache>();
        services.TryAddSingleton<IClipboardReader, NullClipboardReader>();
        services.TryAddSingleton<IIdleTimeReader, NullIdleTimeReader>();
        services.TryAddSingleton<IBrowserUrlReader, NullBrowserUrlReader>();
        return services;
    }
}
