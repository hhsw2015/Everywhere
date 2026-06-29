using System.Net;
using System.Net.Sockets;
using Everywhere.Interop;
using Everywhere.Interop.Whiteboard;
using Everywhere.Mcp.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Everywhere.Mcp.Snapshot;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Transport;

/// <summary>
/// Background host that runs a Kestrel listener for the streamable-HTTP MCP transport
/// (<c>http://localhost:&lt;port&gt;/mcp</c>) inside the GUI process. The port comes from
/// <see cref="EverywhereMcpHttpOptions"/>; on AddressInUseException the host walks
/// <c>Port..Port+MaxPortFallbacks</c> before giving up so a second Everywhere instance
/// doesn't crash the first one's GUI.
/// </summary>
public sealed class EverywhereMcpHttpHost : IHostedService, IAsyncDisposable
{
    private readonly EverywhereMcpHttpOptions _options;
    private readonly IServiceProvider _parentServices;
    private readonly ILogger<EverywhereMcpHttpHost> _logger;
    private readonly object _lifecycleLock = new();
    private WebApplication? _app;
    private int _boundPort;
    private bool _disposed;

    // Resolved once at construction so a missing parent registration fails fast — not silently
    // swallowed by the per-port retry loop.
    private readonly SessionStore _sessions;
    private readonly PickStash _pickStash;
    private readonly WhiteboardStash _whiteboardStash;
    private readonly AnnotationStash _annotationStash;
    private readonly IVisualElementContext _visualContext;
    private readonly IInputSimulator _input;
    private readonly FocusBorrow _focusBorrow;
    private readonly SelectionCache _selectionCache;
    private readonly IClipboardReader _clipboard;
    private readonly IIdleTimeReader _idle;
    private readonly IBrowserUrlReader _browserUrl;
    private readonly IFinderReader _finder;
    private readonly IBrowserTabsReader _browserTabs;
    private readonly Everywhere.Mcp.CursorOverlay.ITargetWindowHighlighter _highlighter;
    // Optional: present on macOS by default (registered in Mac/Program.cs);
    // null when EVERYWHERE_USE_OCCU=0 disables registration, or on
    // Windows / Linux where no backend is wired yet. The eight automation
    // tools hard-error with OccuRequired when this is null — there is no
    // C# fallback path anymore (retired in v0.9.138).
    private readonly IAxBridgeBackend? _axBridgeBackend;

    public EverywhereMcpHttpHost(
        EverywhereMcpHttpOptions options,
        IServiceProvider parentServices,
        ILogger<EverywhereMcpHttpHost> logger)
    {
        _options = options;
        _parentServices = parentServices;
        _logger = logger;

        _sessions = parentServices.GetRequiredService<SessionStore>();
        _pickStash = parentServices.GetRequiredService<PickStash>();
        _whiteboardStash = parentServices.GetRequiredService<WhiteboardStash>();
        _annotationStash = parentServices.GetRequiredService<AnnotationStash>();
        _visualContext = parentServices.GetRequiredService<IVisualElementContext>();
        _input = parentServices.GetRequiredService<IInputSimulator>();
        _focusBorrow = parentServices.GetRequiredService<FocusBorrow>();
        _selectionCache = parentServices.GetRequiredService<SelectionCache>();
        _clipboard = parentServices.GetRequiredService<IClipboardReader>();
        _idle = parentServices.GetRequiredService<IIdleTimeReader>();
        _browserUrl = parentServices.GetRequiredService<IBrowserUrlReader>();
        _finder = parentServices.GetRequiredService<IFinderReader>();
        _browserTabs = parentServices.GetRequiredService<IBrowserTabsReader>();
        _highlighter = parentServices.GetService<Everywhere.Mcp.CursorOverlay.ITargetWindowHighlighter>()
            ?? new Everywhere.Mcp.CursorOverlay.NoopTargetWindowHighlighter();
        _axBridgeBackend = parentServices.GetService<IAxBridgeBackend>();
    }

    public int BoundPort => Volatile.Read(ref _boundPort);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Everywhere MCP HTTP transport disabled by configuration.");
            return;
        }

        // The whole port-walk loop holds _lifecycleLock so that a concurrent StopAsync
        // either runs entirely before us (and then we start cleanly) or entirely after
        // us (and tears down whatever we just bound). Without the lock, an interleaved
        // StopAsync between two retry iterations leaves a started Kestrel without an owner.
        Monitor.Enter(_lifecycleLock);
        try
        {
            if (_disposed) return;

            Exception? lastError = null;
            for (var port = _options.Port; port <= _options.Port + _options.MaxPortFallbacks; port++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_disposed) return;

                WebApplication? candidate = null;
                try
                {
                    candidate = BuildApp(port);
                    await candidate.StartAsync(cancellationToken);
                    _app = candidate;
                    Volatile.Write(ref _boundPort, port);
                    _logger.LogInformation(
                        "Everywhere MCP HTTP transport bound to http://localhost:{Port}/mcp", port);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    if (candidate is not null) await candidate.DisposeAsync();
                    throw;
                }
                catch (IOException ex) when (IsAddressInUse(ex))
                {
                    lastError = ex;
                    if (candidate is not null) await candidate.DisposeAsync();
                    _logger.LogWarning("Port {Port} is in use, trying next.", port);
                }
                catch (SocketException ex) when (IsAddressInUse(ex))
                {
                    lastError = ex;
                    if (candidate is not null) await candidate.DisposeAsync();
                    _logger.LogWarning("Port {Port} is in use, trying next.", port);
                }
                catch
                {
                    if (candidate is not null) await candidate.DisposeAsync();
                    throw;
                }
            }

            throw new InvalidOperationException(
                $"Could not bind Everywhere MCP HTTP transport to ports {_options.Port}..{_options.Port + _options.MaxPortFallbacks}.",
                lastError);
        }
        finally
        {
            Monitor.Exit(_lifecycleLock);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        WebApplication? toStop;
        lock (_lifecycleLock)
        {
            toStop = _app;
            _app = null;
            Volatile.Write(ref _boundPort, 0);
        }
        if (toStop is not null)
        {
            await toStop.StopAsync(cancellationToken);
            await toStop.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        WebApplication? toDispose;
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            toDispose = _app;
            _app = null;
            Volatile.Write(ref _boundPort, 0);
        }
        if (toDispose is not null)
        {
            await toDispose.DisposeAsync();
        }
    }

    private WebApplication BuildApp(int port)
    {
        // Empty builder — don't pull ASPNETCORE_*/appsettings.json/CLI args from the parent
        // GUI process; the embedded listener should run with explicit, minimal config.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(opts => opts.ListenLocalhost(port));
        // Logging: clear inner providers, then re-share the parent
        // ILoggerFactory so anything resolved from the inner DI
        // (OpenDiaToolSync, etc.) writes to Everywhere's normal sinks.
        // Without the share, every inner log line is silently dropped.
        builder.Logging.ClearProviders();
        var parentLoggerFactory = _parentServices.GetService<ILoggerFactory>();
        if (parentLoggerFactory is not null)
        {
            builder.Services.RemoveAll<ILoggerFactory>();
            builder.Services.AddSingleton(parentLoggerFactory);
        }
        builder.Services.AddRoutingCore();

        // Share parent singletons; lifecycle stays with the parent provider.
        builder.Services.AddSingleton(_sessions);
        builder.Services.AddSingleton(_pickStash);
        builder.Services.AddSingleton(_whiteboardStash);
        builder.Services.AddSingleton(_annotationStash);
        builder.Services.AddSingleton(_visualContext);
        builder.Services.AddSingleton(_input);
        builder.Services.AddSingleton(_focusBorrow);
        builder.Services.AddSingleton(_highlighter);
        builder.Services.AddSingleton(_selectionCache);
        builder.Services.AddSingleton(_clipboard);
        // IClipboardWriter (Mac NSPasteboard writer) + OpenDiaBridge must
        // also be forwarded so ClipboardTools / BatchTool constructors
        // can resolve them; previously DI threw a generic
        // 'An error occurred invoking <tool>' on every call.
        var clipboardWriter = _parentServices.GetService<Everywhere.Mcp.Input.IClipboardWriter>();
        if (clipboardWriter is not null) builder.Services.AddSingleton(clipboardWriter);
        var openDiaBridge = _parentServices.GetService<OpenDia.OpenDiaBridge>();
        if (openDiaBridge is not null) builder.Services.AddSingleton(openDiaBridge);
        // MetaTools (list_more_tools / call_tool) is an instance
        // [McpServerToolType] like BatchTool/ClipboardTools — register here
        // so the SDK constructor injection resolves the optional bridge.
        builder.Services.AddSingleton<Tools.MetaTools>();
        builder.Services.AddSingleton(_idle);
        builder.Services.AddSingleton(_browserUrl);
        builder.Services.AddSingleton(_finder);
        builder.Services.AddSingleton(_browserTabs);
        // Forward optional OCCU AX backend (may be null on non-macOS or
        // when EVERYWHERE_USE_OCCU is unset). Tools resolve via
        // GetService — null = fall back to the C# AX path.
        if (_axBridgeBackend is not null)
            builder.Services.AddSingleton(_axBridgeBackend);

        var mcpBuilder = builder.Services
            .AddMcpServer(opts =>
            {
                opts.ServerInfo = new()
                {
                    Name = "everywhere",
                    Version = typeof(EverywhereMcpHttpHost).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                };
            })
            .WithHttpTransport(o => o.Stateless = true)
            .WithToolsFromAssembly(typeof(EverywhereMcpHttpHost).Assembly);

        // Augment static tools with the live opendia browser tool list.
        // ListToolsHandler / CallToolHandler are called AFTER the static
        // ToolCollection per SDK semantics, so we can append our dynamic
        // browser_* tools without touching the static set.
        //
        // Long-tail browser_* tools are hidden behind the CoreToolGate to
        // shrink the system-prompt token cost (~25K → ~5K). The agent
        // reaches them via the meta tools list_more_tools / call_tool
        // (see Tools/MetaTools.cs). Native tools are NOT gated — their
        // total surface is small and they are the agent's perception
        // layer, so the description-token budget is well-spent.
        // Set EVERYWHERE_MCP_FULL=1 to disable the gate entirely (debug,
        // bench parity tests).
        var bridgeForHandler = _parentServices.GetService<OpenDia.OpenDiaBridge>();
        if (bridgeForHandler is not null)
        {
            mcpBuilder
                .WithListToolsHandler((ctx, ct) =>
                {
                    var result = new ModelContextProtocol.Protocol.ListToolsResult();
                    foreach (var spec in bridgeForHandler.AvailableTools)
                    {
                        var t = OpenDia.OpenDiaToolListBuilder.BuildTool(spec);
                        if (t is null) continue;
                        if (CoreToolGate.ShouldFilter(t.Name)) continue;
                        result.Tools.Add(t);
                    }
                    return ValueTask.FromResult(result);
                })
                .AddListToolsFilter(next => async (ctx, ct) =>
                {
                    // Filter the static [McpServerTool] surface (native tools)
                    // through the same gate. Hidden native tools stay reachable
                    // via call_tool — see Tools/MetaTools.cs.
                    var result = await next(ctx, ct).ConfigureAwait(false);
                    if (CoreToolGate.FilterEnabled && result?.Tools is { Count: > 0 } tools)
                    {
                        var kept = tools.Where(t => !CoreToolGate.ShouldFilter(t.Name)).ToList();
                        tools.Clear();
                        foreach (var t in kept) tools.Add(t);
                    }
                    return result!;
                })
                .WithCallToolHandler(async (ctx, ct) =>
                {
                    var name = ctx.Params?.Name ?? string.Empty;
                    if (!name.StartsWith(OpenDia.OpenDiaToolListBuilder.Prefix, StringComparison.Ordinal))
                    {
                        return new ModelContextProtocol.Protocol.CallToolResult
                        {
                            IsError = true,
                            Content = [new ModelContextProtocol.Protocol.TextContentBlock
                            {
                                Text = $"Unknown tool: {name}",
                            }],
                        };
                    }
                    var origName = name.Substring(OpenDia.OpenDiaToolListBuilder.Prefix.Length);
                    System.Text.Json.Nodes.JsonNode? args = null;
                    var argsDict = ctx.Params?.Arguments;
                    if (argsDict is { Count: > 0 })
                    {
                        var obj = new System.Text.Json.Nodes.JsonObject();
                        foreach (var (k, v) in argsDict)
                            obj[k] = System.Text.Json.Nodes.JsonNode.Parse(v.GetRawText());
                        args = obj;
                    }
                    _logger.LogInformation(
                        "OpenDia: invoking browser tool {Name} (orig={Orig}) args={Args}",
                        name, origName, args?.ToJsonString() ?? "(none)");
                    try
                    {
                        var raw = await bridgeForHandler.CallToolAsync(origName, args, ct: ct);
                        _logger.LogInformation(
                            "OpenDia: {Name} returned ok ({Bytes} bytes)",
                            name, raw?.ToJsonString().Length ?? 0);
                        return new ModelContextProtocol.Protocol.CallToolResult
                        {
                            Content = [new ModelContextProtocol.Protocol.TextContentBlock
                            {
                                Text = raw is null ? "{}" : raw.ToJsonString(),
                            }],
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "OpenDia: {Name} failed: {Msg}", name, ex.Message);
                        return new ModelContextProtocol.Protocol.CallToolResult
                        {
                            IsError = true,
                            Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = ex.Message }],
                        };
                    }
                });
        }

        var app = builder.Build();

        // Defense-in-depth: trust the actual TCP peer, not the user-controlled Host header.
        // Also gate the Origin header so a browser can't drive us via DNS rebinding (MCP §8.2).
        app.Use(LoopbackOnly(port));

        app.MapMcp("/mcp");
        return app;
    }

    private static Func<HttpContext, RequestDelegate, Task> LoopbackOnly(int port) => async (ctx, next) =>
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsync("Everywhere MCP HTTP only accepts loopback connections.");
            return;
        }

        if (ctx.Request.Headers.TryGetValue("Origin", out var origin) && origin.Count > 0)
        {
            var raw = origin.ToString();
            if (!IsAllowedOrigin(raw, port))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsync("Origin not allowed.");
                return;
            }
        }

        await next(ctx);
    };

    private static bool IsAllowedOrigin(string raw, int port)
    {
        // Reject the literal "null" origin (file://, sandboxed iframes, opaque origins).
        if (string.Equals(raw, "null", StringComparison.Ordinal)) return false;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return false;

        // Accept the configured port AND the default 80/443 fallthrough on the off-chance
        // a user proxies through a local reverse proxy.
        if (uri.Port != port && uri.Port != 80 && uri.Port != 443) return false;

        var host = uri.Host;
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host == "127.0.0.1"
            || host == "::1"
            || host == "[::1]";
    }

    private static bool IsAddressInUse(Exception ex)
    {
        // Walk the inner-exception chain — Kestrel typically wraps the SocketException
        // in an IOException ("Failed to bind to address ...").
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is SocketException se && se.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                return true;
            }
        }
        return false;
    }
}
