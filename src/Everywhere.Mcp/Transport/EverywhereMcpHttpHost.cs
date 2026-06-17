using Everywhere.Interop;
using Everywhere.Mcp.Input;
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
/// <see cref="EverywhereMcpHttpOptions"/>; on bind conflict the host walks
/// <c>Port..Port+MaxPortFallbacks</c> before giving up so a second Everywhere instance
/// doesn't crash the first one's GUI.
/// </summary>
public sealed class EverywhereMcpHttpHost : IHostedService, IAsyncDisposable
{
    private readonly EverywhereMcpHttpOptions _options;
    private readonly IServiceProvider _parentServices;
    private readonly ILogger<EverywhereMcpHttpHost> _logger;
    private WebApplication? _app;
    private int _boundPort;

    public EverywhereMcpHttpHost(
        EverywhereMcpHttpOptions options,
        IServiceProvider parentServices,
        ILogger<EverywhereMcpHttpHost> logger)
    {
        _options = options;
        _parentServices = parentServices;
        _logger = logger;
    }

    public int BoundPort => _boundPort;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Everywhere MCP HTTP transport disabled by configuration.");
            return;
        }

        Exception? lastError = null;
        for (var port = _options.Port; port <= _options.Port + _options.MaxPortFallbacks; port++)
        {
            try
            {
                _app = BuildApp(port);
                await _app.StartAsync(cancellationToken);
                _boundPort = port;
                _logger.LogInformation(
                    "Everywhere MCP HTTP transport bound to http://localhost:{Port}/mcp", port);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (_app is not null)
                {
                    await _app.DisposeAsync();
                    _app = null;
                }
                _logger.LogWarning(ex, "Could not bind MCP HTTP on port {Port}, trying next.", port);
            }
        }

        throw new InvalidOperationException(
            $"Could not bind Everywhere MCP HTTP transport to ports {_options.Port}..{_options.Port + _options.MaxPortFallbacks}.",
            lastError);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            await _app.StopAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
            _app = null;
        }
    }

    private WebApplication BuildApp(int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(opts => opts.ListenLocalhost(port));

        // Share the singletons that already live in the parent GUI process — tool methods
        // reach them via DI parameters during a JSON-RPC tool call.
        builder.Services.AddSingleton(_parentServices.GetRequiredService<SessionStore>());
        builder.Services.AddSingleton(_parentServices.GetRequiredService<IVisualElementContext>());
        builder.Services.AddSingleton(_parentServices.GetRequiredService<IInputSimulator>());
        builder.Services.AddSingleton(_parentServices.GetRequiredService<FocusBorrow>());

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

        var app = builder.Build();

        // Spec §8.2: localhost-only ACL.
        app.Use(async (ctx, next) =>
        {
            var host = ctx.Request.Host.Host;
            if (!IsLocalhost(host))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsync("Everywhere MCP HTTP only accepts loopback connections.");
                return;
            }
            await next();
        });

        app.MapMcp("/mcp");
        return app;
    }

    private static bool IsLocalhost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host == "127.0.0.1"
        || host == "[::1]"
        || host == "::1";
}
