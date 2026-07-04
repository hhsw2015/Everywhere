using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp.Connector;

/// <summary>
/// SPEC docs/specs/everywhere-connector.md §9 — Kestrel wiring for the
/// upstream open-connector Web Console + the REST endpoints it hits.
///
/// Mounted paths:
/// <list type="bullet">
///   <item><c>/connector-ui/*</c> — static assets from the vite-built SPA.</item>
///   <item><c>GET /api/providers</c> — provider index.</item>
///   <item><c>GET /api/providers/:service</c> — one provider's actions + auth defs.</item>
///   <item><c>GET /api/connections</c> — configured connections (no secrets).</item>
///   <item><c>PUT /api/connections/:service</c> — store an api_key connection.</item>
///   <item><c>DELETE /api/connections/:service</c> — remove a connection.</item>
///   <item><c>POST /v1/actions/:actionId</c> — execute (mirrors upstream shape).</item>
/// </list>
///
/// OAuth endpoints (Phase 3.5) live in a separate module — see
/// spec §7 Phase 3.
/// </summary>
internal static class ConnectorHttpEndpoints
{
    public static void Map(WebApplication app, IServiceProvider parentServices)
    {
        var log = app.Services.GetService<ILoggerFactory>()?.CreateLogger("ConnectorHttp");

        // Static file middleware for the Web Console. Bundled to
        // Resources/connector/web/ at publish time by
        // Build.ConnectorBundle.targets.
        var env = app.Services.GetService<IWebHostEnvironment>();
        var webRoot = ResolveWebRoot(env);
        if (webRoot is not null)
        {
            var provider = new PhysicalFileProvider(webRoot);
            // Wire /connector-ui → dist. Includes default files (index.html).
            app.UseDefaultFiles(new DefaultFilesOptions
            {
                FileProvider = provider,
                RequestPath = "/connector-ui",
                DefaultFileNames = new[] { "index.html" },
            });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = provider,
                RequestPath = "/connector-ui",
                ContentTypeProvider = new FileExtensionContentTypeProvider(),
            });
            // Client-side router fallback — any /connector-ui/* path that
            // doesn't map to a real file returns index.html so react-router
            // handles it in the browser.
            app.MapFallback("/connector-ui/{*rest}", async ctx =>
            {
                var indexPath = Path.Combine(webRoot, "index.html");
                if (!File.Exists(indexPath))
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.SendFileAsync(indexPath);
            });
            log?.LogInformation("connector: web console mounted at /connector-ui/ from {Root}", webRoot);
        }
        else
        {
            log?.LogInformation("connector: web console not found on disk — /connector-ui returns 404");
        }

        // Parent-container services (both DI containers share the runtime).
        var runtime = parentServices.GetService<ConnectorRuntime>();
        var store = parentServices.GetService<JsonCredentialStore>();

        // --- /api/providers ------------------------------------------------
        app.MapGet("/api/providers", (HttpContext ctx) =>
        {
            if (runtime is null) return NotConfigured(ctx);
            var manifest = runtime.ListManifest();
            var arr = new JsonArray();
            foreach (var svc in manifest.Services)
            {
                arr.Add(new JsonObject
                {
                    ["service"] = svc.Service,
                    ["displayName"] = svc.DisplayName,
                    ["categories"] = ToJsonArray(svc.Categories),
                    ["authTypes"] = ToJsonArray(svc.AuthTypes),
                    ["homepageUrl"] = svc.HomepageUrl,
                    ["actionCount"] = svc.Actions.Count,
                });
            }
            return WriteJson(ctx, new JsonObject
            {
                ["schemaVersion"] = "1",
                ["upstreamSha"] = manifest.UpstreamSha,
                ["providers"] = arr,
            });
        });

        app.MapGet("/api/providers/{service}", (HttpContext ctx, string service) =>
        {
            if (runtime is null) return NotConfigured(ctx);
            var manifest = runtime.ListManifest();
            var svc = manifest.Services.FirstOrDefault(s => string.Equals(s.Service, service, StringComparison.OrdinalIgnoreCase));
            if (svc is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return WriteJson(ctx, new JsonObject
                {
                    ["error"] = $"service '{service}' not in catalog",
                    ["code"] = "RUNTIME_NOT_FOUND",
                });
            }
            var actions = new JsonArray();
            foreach (var a in svc.Actions)
            {
                actions.Add(new JsonObject
                {
                    ["id"] = a.Id,
                    ["service"] = a.Service,
                    ["name"] = a.Name,
                    ["description"] = a.Description,
                    ["requiredScopes"] = ToJsonArray(a.RequiredScopes),
                    ["inputSchema"] = a.InputSchema?.DeepClone(),
                    ["outputSchema"] = a.OutputSchema?.DeepClone(),
                });
            }
            return WriteJson(ctx, new JsonObject
            {
                ["schemaVersion"] = "1",
                ["service"] = svc.Service,
                ["displayName"] = svc.DisplayName,
                ["categories"] = ToJsonArray(svc.Categories),
                ["authTypes"] = ToJsonArray(svc.AuthTypes),
                ["homepageUrl"] = svc.HomepageUrl,
                ["actions"] = actions,
            });
        });

        // --- /api/connections ---------------------------------------------
        app.MapGet("/api/connections", (HttpContext ctx) =>
        {
            if (store is null) return NotConfigured(ctx);
            var arr = new JsonArray();
            foreach (var c in store.List())
            {
                arr.Add(new JsonObject
                {
                    ["service"] = c.Service,
                    ["authType"] = c.AuthType,
                    ["displayName"] = c.DisplayName,
                    ["accountId"] = c.AccountId,
                });
            }
            return WriteJson(ctx, new JsonObject
            {
                ["schemaVersion"] = "1",
                ["connections"] = arr,
            });
        });

        app.MapMethods("/api/connections/{service}", new[] { "PUT" }, async (HttpContext ctx, string service) =>
        {
            if (store is null) { await NotConfigured(ctx); return; }
            var body = await ReadJsonBody(ctx);
            var authType = body?["authType"]?.GetValue<string>();
            if (!string.Equals(authType, "api_key", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJson(ctx, new JsonObject
                {
                    ["error"] = "Phase 3 only supports authType=api_key. OAuth lands in Phase 3.5.",
                    ["code"] = "invalid_input",
                });
                return;
            }
            var apiKey = body?["values"]?["apiKey"]?.GetValue<string>()
                         ?? body?["apiKey"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJson(ctx, new JsonObject
                {
                    ["error"] = "values.apiKey (or top-level apiKey) is required",
                    ["code"] = "invalid_input",
                });
                return;
            }
            var displayName = body?["profile"]?["displayName"]?.GetValue<string>()
                              ?? body?["displayName"]?.GetValue<string>();
            store.SetApiKey(service, apiKey, displayName);
            await WriteJson(ctx, new JsonObject
            {
                ["schemaVersion"] = "1",
                ["service"] = service,
                ["authType"] = "api_key",
            });
        });

        app.MapDelete("/api/connections/{service}", async (HttpContext ctx, string service) =>
        {
            if (store is null) { await NotConfigured(ctx); return; }
            var removed = store.Delete(service);
            await WriteJson(ctx, new JsonObject
            {
                ["schemaVersion"] = "1",
                ["service"] = service,
                ["removed"] = removed,
            });
        });

        // --- /v1/actions/:actionId — mirrors upstream shape ---------------
        app.MapPost("/v1/actions/{actionId}", async (HttpContext ctx, string actionId) =>
        {
            if (runtime is null) { await NotConfigured(ctx); return; }
            var body = await ReadJsonBody(ctx);
            var input = body?["input"] as JsonObject ?? body as JsonObject ?? new JsonObject();
            // upstream action ids are "<service>.<name>".
            var dot = actionId.IndexOf('.');
            if (dot <= 0 || dot == actionId.Length - 1)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJson(ctx, new JsonObject
                {
                    ["error"] = "actionId must be '<service>.<name>'",
                    ["code"] = "invalid_input",
                });
                return;
            }
            var service = actionId[..dot];
            var name = actionId[(dot + 1)..];
            var envelope = await runtime.InvokeAsync(service, name, input, ctx.RequestAborted);
            var okNode = envelope["ok"];
            var ok = okNode is not null && okNode.GetValue<bool>();
            ctx.Response.StatusCode = ok ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest;
            await WriteJson(ctx, envelope);
        });
    }

    private static string? ResolveWebRoot(IWebHostEnvironment? env)
    {
        // Publish layout: {baseDir}/Resources/connector/web/
        var baseDir = AppContext.BaseDirectory;
        foreach (var probe in new[]
                 {
                     Path.Combine(baseDir, "Resources", "connector", "web"),
                     Path.Combine(baseDir, "..", "Resources", "connector", "web"),
                 })
        {
            var canon = Path.GetFullPath(probe);
            if (File.Exists(Path.Combine(canon, "index.html"))) return canon;
        }
        // Dev fallback — walk up to repo root.
        var dir = new DirectoryInfo(baseDir);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "3rd", "open-connector", "dist", "web");
            if (File.Exists(Path.Combine(candidate, "index.html"))) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static Task NotConfigured(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return WriteJson(ctx, new JsonObject
        {
            ["error"] = "connector runtime not configured in this daemon",
            ["code"] = "RUNTIME_HOST_ERROR",
        });
    }

    private static async Task<JsonNode?> ReadJsonBody(HttpContext ctx)
    {
        if (!ctx.Request.HasFormContentType && ctx.Request.ContentLength.GetValueOrDefault() == 0
            && !string.Equals(ctx.Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            // Try to parse a body anyway — some clients omit Content-Length.
        }
        using var reader = new StreamReader(ctx.Request.Body);
        var raw = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return JsonNode.Parse(raw); }
        catch (JsonException) { return null; }
    }

    private static Task WriteJson(HttpContext ctx, JsonNode payload)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        return ctx.Response.WriteAsync(payload.ToJsonString());
    }

    private static JsonArray ToJsonArray(IEnumerable<string>? items)
    {
        var arr = new JsonArray();
        if (items is null) return arr;
        foreach (var s in items) arr.Add((JsonNode)JsonValue.Create(s ?? "")!);
        return arr;
    }
}
