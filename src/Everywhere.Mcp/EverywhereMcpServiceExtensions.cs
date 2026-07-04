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
        // SPEC docs/specs/opendia-cebian-merge.md §Phase 4 — chat bus wraps
        // OpenDiaBridge for chat_* frames + subscribe long-poll. Registered
        // as singleton so subscriber cursors survive across MCP calls.
        services.TryAddSingleton<OpenDia.OpenDiaChatBus>();
        services.TryAddSingleton<Tools.ChatBusTools>();
        // Instance-based [McpServerToolType] classes need to be in DI
        // for the MCP SDK to resolve their constructors. (Static
        // [McpServerToolType] don't need this.)
        services.TryAddSingleton<Tools.BatchTool>();
        services.TryAddSingleton<Tools.ClipboardTools>();
        // Meta tools (list_more_tools / call_tool) — depend on OpenDiaBridge
        // for the long-tail browser_* surface listing + dispatch.
        services.TryAddSingleton<Tools.MetaTools>();
        // Web search/fetch via the user's configured Everywhere provider —
        // depends on IWebSearchService + IHttpClientFactory registered in
        // Everywhere.Core. Inner host re-registers on the SDK container
        // (see EverywhereMcpHttpHost).
        services.TryAddSingleton<Tools.WebSearchTool>();
        // SPEC docs/specs/everywhere-opencli-adapters.md — OpenCLI adapter
        // runtime. The V8 engine is lazy-booted on first opencli_* call, so
        // Everywhere installs that never use it pay no startup cost.
        services.TryAddSingleton<OpenCli.OpenCliRuntime>(sp =>
        {
            var baseDir = AppContext.BaseDirectory;
            // Probe the published layout first, then macOS Contents/Resources
            // (the .app bundler diverts <Content> items there), then a dev
            // fallback that walks up to find 3rd/opencli/ in the repo root.
            string? clisDir = null;
            foreach (var probe in new[]
                     {
                         Path.Combine(baseDir, "Resources", "opencli"),
                         Path.Combine(baseDir, "..", "Resources", "opencli"),
                         Path.Combine(baseDir, "..", "Resources", "Resources", "opencli"),
                     })
            {
                var canon = Path.GetFullPath(probe);
                if (Directory.Exists(Path.Combine(canon, "clis")))
                {
                    clisDir = canon;
                    break;
                }
            }
            if (clisDir is null)
            {
                var dir = new DirectoryInfo(baseDir);
                while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "3rd", "opencli", "clis")))
                    dir = dir.Parent;
                if (dir != null) clisDir = Path.Combine(dir.FullName, "3rd", "opencli");
            }
            // Last resort — point at the (likely missing) publish layout so
            // the runtime still constructs and List/Run return an empty
            // registry instead of crashing DI at boot.
            clisDir ??= Path.Combine(baseDir, "Resources", "opencli");
            return new OpenCli.OpenCliRuntime(
                Path.Combine(clisDir, "clis"),
                Path.Combine(clisDir, "cli-manifest.json"),
                new HttpClient(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<OpenCli.OpenCliRuntime>>());
        });
        services.TryAddSingleton<Tools.OpenCliTools>();

        // SPEC docs/specs/everywhere-connector.md — open-connector provider
        // runtime. Separate V8 isolate from OpenCLI (§3.1). Phase 2:
        // env-var override chained over a JSON store under
        // ~/.everywhere/connector/connections.json. The store is exposed
        // as its concrete type so ConnectorTools' write/list ops can
        // reach it without a separate interface.
        services.TryAddSingleton<Connector.JsonCredentialStore>(sp => new Connector.JsonCredentialStore());
        // Phase 8 — transit file store used by provider actions that
        // upload/download binary payloads. Base URL is derived at
        // request time from the request Host header.
        services.TryAddSingleton<Connector.TransitFileStore>(sp => new Connector.TransitFileStore(
            baseUrlFactory: () => "http://127.0.0.1:7878",
            log: sp.GetService<Microsoft.Extensions.Logging.ILogger<Connector.TransitFileStore>>()));
        services.TryAddSingleton<Connector.ICredentialResolver>(sp =>
            new Connector.ChainedCredentialResolver(
                new Connector.EnvironmentCredentialResolver(),
                sp.GetRequiredService<Connector.JsonCredentialStore>()));
        services.TryAddSingleton<Connector.ConnectorRuntime>(sp =>
        {
            var baseDir = AppContext.BaseDirectory;
            string? bundleDir = null;
            foreach (var probe in new[]
                     {
                         Path.Combine(baseDir, "Resources", "connector"),
                         Path.Combine(baseDir, "..", "Resources", "connector"),
                         Path.Combine(baseDir, "..", "Resources", "Resources", "connector"),
                     })
            {
                var canon = Path.GetFullPath(probe);
                if (File.Exists(Path.Combine(canon, "connector.bundle.js")))
                {
                    bundleDir = canon;
                    break;
                }
            }
            if (bundleDir is null)
            {
                // Dev fallback — walk up to repo root, look for 3rd/open-connector/dist.
                var dir = new DirectoryInfo(baseDir);
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "3rd", "open-connector", "dist", "connector.bundle.js")))
                    dir = dir.Parent;
                if (dir != null) bundleDir = Path.Combine(dir.FullName, "3rd", "open-connector", "dist");
            }
            bundleDir ??= Path.Combine(baseDir, "Resources", "connector");
            var creds = sp.GetRequiredService<Connector.ICredentialResolver>();
            return new Connector.ConnectorRuntime(
                bundleDir,
                new HttpClient(),
                creds,
                sp.GetService<Microsoft.Extensions.Logging.ILogger<Connector.ConnectorRuntime>>(),
                sp.GetService<Connector.TransitFileStore>());
        });
        services.TryAddSingleton<Tools.ConnectorTools>();

        // OAuth flow service (Phase 3.5). Uses its own HttpClient — token
        // exchange endpoints are all provider-controlled URLs, no shared
        // pool needed. Phase 6 wires the refresher into ConnectorRuntime.
        services.TryAddSingleton<Connector.OAuthFlowService>(sp =>
        {
            var flow = new Connector.OAuthFlowService(
                sp.GetRequiredService<Connector.ConnectorRuntime>(),
                sp.GetRequiredService<Connector.JsonCredentialStore>(),
                new HttpClient(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<Connector.OAuthFlowService>>());
            sp.GetRequiredService<Connector.ConnectorRuntime>().OAuthRefresher = flow;
            return flow;
        });
        // SPEC docs/specs/everywhere-self-expanding.md Phase 1 — capture session store
        // plus the observation MCP tools. Store is singleton (in-memory,
        // server-restart invalidates per spec).
        services.TryAddSingleton<OpenCli.Observation.CaptureSessionStore>(sp =>
            new OpenCli.Observation.CaptureSessionStore());
        services.TryAddSingleton<Tools.CaptureTools>();
        // SPEC Phase 3 — site memory store. Prod uses SystemClock; tests
        // pass a FakeClock via a scoped MemoryStore constructor.
        services.TryAddSingleton<OpenCli.Memory.MemoryStore>(sp => new OpenCli.Memory.MemoryStore());
        services.TryAddSingleton<Tools.MemoryTools>();
        // SPEC Phase 4 — adapter lint / strategy-note tools.
        services.TryAddSingleton<Tools.GateTools>();
        // SPEC Phase 2 — analysis tools consume CaptureSession by id.
        services.TryAddSingleton<Tools.AnalysisTools>();
        // SPEC Phase 5 — generator + local registry.
        services.TryAddSingleton<Tools.GeneratorTools>();
        // SPEC Phase 6 — session activations + BM25 search + tier gate.
        services.TryAddSingleton<Meta.SessionActivations>();
        services.TryAddSingleton<Tools.SearchTools>();
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
