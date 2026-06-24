using System.Net;
using System.Net.Sockets;
using Everywhere.Interop;
using Everywhere.Interop.Whiteboard;
using Everywhere.Mcp.Input;
using Microsoft.Extensions.DependencyInjection;
using Everywhere.Mcp.Snapshot;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IVisualElementContext _visualContext;
    private readonly IInputSimulator _input;
    private readonly FocusBorrow _focusBorrow;
    private readonly SelectionCache _selectionCache;
    private readonly IClipboardReader _clipboard;
    private readonly IIdleTimeReader _idle;
    private readonly IBrowserUrlReader _browserUrl;
    private readonly IFinderReader _finder;
    private readonly IBrowserTabsReader _browserTabs;

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
        _visualContext = parentServices.GetRequiredService<IVisualElementContext>();
        _input = parentServices.GetRequiredService<IInputSimulator>();
        _focusBorrow = parentServices.GetRequiredService<FocusBorrow>();
        _selectionCache = parentServices.GetRequiredService<SelectionCache>();
        _clipboard = parentServices.GetRequiredService<IClipboardReader>();
        _idle = parentServices.GetRequiredService<IIdleTimeReader>();
        _browserUrl = parentServices.GetRequiredService<IBrowserUrlReader>();
        _finder = parentServices.GetRequiredService<IFinderReader>();
        _browserTabs = parentServices.GetRequiredService<IBrowserTabsReader>();
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
        builder.Logging.ClearProviders();
        builder.Services.AddRoutingCore();

        // Share parent singletons; lifecycle stays with the parent provider.
        builder.Services.AddSingleton(_sessions);
        builder.Services.AddSingleton(_pickStash);
        builder.Services.AddSingleton(_whiteboardStash);
        builder.Services.AddSingleton(_visualContext);
        builder.Services.AddSingleton(_input);
        builder.Services.AddSingleton(_focusBorrow);
        builder.Services.AddSingleton(_selectionCache);
        builder.Services.AddSingleton(_clipboard);
        builder.Services.AddSingleton(_idle);
        builder.Services.AddSingleton(_browserUrl);
        builder.Services.AddSingleton(_finder);
        builder.Services.AddSingleton(_browserTabs);

        builder.Services
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

        // Bind the bridge from the parent provider as a regular singleton
        // *into the inner builder*. Without this the inner provider builds
        // its own OpenDiaBridge instance separate from the one the parent
        // initializer started — sync would observe an empty/never-populated
        // bridge.
        var parentBridge = _parentServices.GetService<OpenDia.OpenDiaBridge>();
        _logger.LogInformation(
            "OpenDia: parentBridge resolution {State}",
            parentBridge is null ? "FAILED (null)" : "OK");
        if (parentBridge is not null)
            builder.Services.AddSingleton(parentBridge);

        var app = builder.Build();

        // OpenDia tool sync needs the live McpServerOptions.ToolCollection,
        // which only exists on the inner provider — so it must be resolved
        // from app.Services. Build it manually here (the parent provider
        // doesn't have it registered as that would defeat the purpose).
        try
        {
            if (parentBridge is not null)
            {
                var sync = ActivatorUtilities.CreateInstance<OpenDia.OpenDiaToolSync>(app.Services);
                _logger.LogInformation("OpenDia: invoking ToolSync.Wire() (initial sync + StateChanged subscribe)");
                sync.Wire();
                _logger.LogInformation("OpenDia: ToolSync wired successfully");
            }
            else
            {
                _logger.LogWarning("OpenDia: bridge singleton missing; tool sync skipped.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenDia: tool sync wiring failed; browser tools will not appear in MCP.");
        }

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
