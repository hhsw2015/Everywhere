using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.Interop.Whiteboard;
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
        // Native opendia browser bridge (off by default, enabled via
        // McpServerSettings.OpenDiaEnabled). The bridge is a singleton so
        // status surfaces and the MCP tool sync read its state.
        services.TryAddSingleton<OpenDia.OpenDiaBridge>();
        // Instance-based [McpServerToolType] classes need to be in DI
        // for the MCP SDK to resolve their constructors. (Static
        // [McpServerToolType] don't need this.)
        services.TryAddSingleton<Tools.BatchTool>();
        services.TryAddSingleton<Tools.ClipboardTools>();
        // OpenDiaToolSync needs IOptions<McpServerOptions> from the inner
        // MCP container — it's instantiated from there in
        // EverywhereMcpHttpHost.BuildApp(), not here.
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
        services.TryAddSingleton<WhiteboardStash>();
        services.TryAddSingleton<AnnotationStash>();
        services.TryAddSingleton<IVisualElementContext, EmptyVisualElementContext>();
        // Cursor overlay infra: always-on shared trace channel that
        // anything wanting to observe input can subscribe to. The
        // overlay itself is opt-in (CursorOverlayEnabled). Pre-OCCU
        // line-by-line port lives under Everywhere.Mcp.CursorOverlay.
        services.TryAddSingleton<Everywhere.Mcp.Input.CursorTrace>();
        services.AddSingleton<Everywhere.Mcp.CursorOverlay.CursorOverlayInitializer>();
        services.AddTransient<IAsyncInitializer>(sp =>
            sp.GetRequiredService<Everywhere.Mcp.CursorOverlay.CursorOverlayInitializer>());
        services.AddSingleton<Everywhere.Mcp.CursorOverlay.ITargetWindowHighlighter>(sp =>
            sp.GetRequiredService<Everywhere.Mcp.CursorOverlay.CursorOverlayInitializer>());
        services.TryAddSingleton<IInputSimulator, NotSupportedInputSimulator>();
        services.TryAddSingleton<IFocusBackend, NotSupportedFocusBackend>();
        services.TryAddSingleton<FocusBorrow>();
        services.TryAddSingleton<SelectionCache>();
        services.TryAddSingleton<IClipboardReader, NullClipboardReader>();
        services.TryAddSingleton<IClipboardWriter, NullClipboardWriter>();
        services.TryAddSingleton<IIdleTimeReader, NullIdleTimeReader>();
        services.TryAddSingleton<IBrowserUrlReader, NullBrowserUrlReader>();
        services.TryAddSingleton<IAppleScriptRunner, NullAppleScriptRunner>();
        services.TryAddSingleton<IFinderReader, NullFinderReader>();
        services.TryAddSingleton<IBrowserTabsReader, NullBrowserTabsReader>();
        services.TryAddSingleton<IAppActivator, NullAppActivator>();
        // Default OCR is no-op; platform projects (Everywhere.Mac etc.) replace
        // this with a real engine via AddSingleton<IOcrEngine, MacVisionOcrEngine>().
        services.TryAddSingleton<IOcrEngine, NullOcrEngine>();
        services.TryAddSingleton<ContextStashWriter>();
        services.TryAddSingleton<AutoCaptureService>();
        return services;
    }
}
