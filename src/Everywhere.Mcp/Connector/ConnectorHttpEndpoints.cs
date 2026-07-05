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
            // Handle every /connector-ui/* request in-line here as a
            // terminal middleware so it never falls through to the
            // MCP endpoint routing (which would otherwise catch asset
            // requests via MapFallback and return index.html with
            // Content-Type text/html — breaking the SPA). Static files
            // and the SPA client-side-router fallback both live here.
            app.MapWhen(
                ctx => ctx.Request.Path.StartsWithSegments("/connector-ui"),
                branch =>
                {
                    var provider = new PhysicalFileProvider(webRoot);
                    branch.UseDefaultFiles(new DefaultFilesOptions
                    {
                        FileProvider = provider,
                        RequestPath = "/connector-ui",
                        DefaultFileNames = new[] { "index.html" },
                    });
                    branch.UseStaticFiles(new StaticFileOptions
                    {
                        FileProvider = provider,
                        RequestPath = "/connector-ui",
                        ContentTypeProvider = new FileExtensionContentTypeProvider(),
                    });
                    // Terminal fallback for any unmatched extensionless
                    // path inside /connector-ui/ — SPA client-side router
                    // takes over from index.html.
                    branch.Run(async ctx =>
                    {
                        var path = ctx.Request.Path.Value ?? "";
                        if (Path.HasExtension(path))
                        {
                            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                            return;
                        }
                        var indexPath = Path.Combine(webRoot, "index.html");
                        if (!File.Exists(indexPath))
                        {
                            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                            return;
                        }
                        ctx.Response.ContentType = "text/html; charset=utf-8";
                        await ctx.Response.SendFileAsync(indexPath);
                    });
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

        // --- /api/oauth/configs (Phase 3.5) ------------------------------
        app.MapGet("/api/oauth/configs", (HttpContext ctx) =>
        {
            if (store is null) return NotConfigured(ctx);
            var arr = new JsonArray();
            foreach (var c in store.ListOAuthClients())
            {
                arr.Add(new JsonObject
                {
                    ["service"] = c.Service,
                    ["clientId"] = c.ClientId,
                    ["hasSecret"] = c.HasSecret,
                    ["redirectUri"] = c.RedirectUri,
                });
            }
            return WriteJson(ctx, new JsonObject
            {
                ["schemaVersion"] = "1",
                ["oauthClients"] = arr,
            });
        });

        app.MapPost("/api/oauth/configs/{service}", async (HttpContext ctx, string service) =>
        {
            if (store is null) { await NotConfigured(ctx); return; }
            var body = await ReadJsonBody(ctx);
            var clientId = body?["clientId"]?.GetValue<string>();
            var clientSecret = body?["clientSecret"]?.GetValue<string>();
            var redirectUri = body?["redirectUri"]?.GetValue<string>()
                              ?? DefaultRedirectUri(ctx);
            if (string.IsNullOrWhiteSpace(clientId))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJson(ctx, new JsonObject { ["error"] = "clientId required", ["code"] = "invalid_input" });
                return;
            }
            store.SetOAuthClient(service, clientId, clientSecret, redirectUri, body?["extra"] as JsonObject);
            await WriteJson(ctx, new JsonObject
            {
                ["schemaVersion"] = "1",
                ["service"] = service,
                ["redirectUri"] = redirectUri,
            });
        });

        app.MapDelete("/api/oauth/configs/{service}", async (HttpContext ctx, string service) =>
        {
            if (store is null) { await NotConfigured(ctx); return; }
            var removed = store.DeleteOAuthClient(service);
            await WriteJson(ctx, new JsonObject
            {
                ["schemaVersion"] = "1",
                ["service"] = service,
                ["removed"] = removed,
            });
        });

        // --- /api/oauth/authorize/:service --------------------------------
        var oauth = parentServices.GetService<OAuthFlowService>();
        app.MapPost("/api/oauth/authorize/{service}", async (HttpContext ctx, string service) =>
        {
            if (oauth is null) { await NotConfigured(ctx); return; }
            try
            {
                var result = oauth.Authorize(service);
                await WriteJson(ctx, new JsonObject
                {
                    ["schemaVersion"] = "1",
                    ["service"] = result.Service,
                    ["url"] = result.Url,
                    ["state"] = result.State,
                    ["redirectUri"] = result.RedirectUri,
                });
            }
            catch (OAuthException ex)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJson(ctx, new JsonObject { ["error"] = ex.Message, ["code"] = ex.Code });
            }
        });

        // --- /api/oauth/callback ------------------------------------------
        // Handler for the redirect the OAuth provider sends the user back
        // to. Renders a plain HTML page with success / failure so users
        // see something in the browser tab.
        app.MapGet("/api/oauth/callback", async (HttpContext ctx) =>
        {
            if (oauth is null) { await NotConfigured(ctx); return; }
            var state = ctx.Request.Query["state"].ToString();
            var code = ctx.Request.Query["code"].ToString();
            var providerErr = ctx.Request.Query["error"].ToString();
            if (!string.IsNullOrEmpty(providerErr))
            {
                await WriteHtml(ctx, StatusCodes.Status400BadRequest,
                    $"<h1>OAuth failed</h1><p>Provider returned: <code>{HtmlEncode(providerErr)}</code></p>");
                return;
            }
            if (string.IsNullOrEmpty(state) || string.IsNullOrEmpty(code))
            {
                await WriteHtml(ctx, StatusCodes.Status400BadRequest,
                    "<h1>OAuth failed</h1><p>Missing state or code query parameters.</p>");
                return;
            }
            try
            {
                var result = await oauth.HandleCallbackAsync(state, code, ctx.RequestAborted);
                await WriteHtml(ctx, StatusCodes.Status200OK,
                    $"<h1>Connected {HtmlEncode(result.Service)}</h1>" +
                    "<p>You can close this tab and return to the Everywhere UI.</p>" +
                    "<script>setTimeout(() => window.close(), 1500);</script>");
            }
            catch (OAuthException ex)
            {
                await WriteHtml(ctx, StatusCodes.Status400BadRequest,
                    $"<h1>OAuth failed</h1><p>{HtmlEncode(ex.Message)}</p>");
            }
            catch (Exception ex)
            {
                log?.LogWarning(ex, "OAuth callback threw");
                await WriteHtml(ctx, StatusCodes.Status500InternalServerError,
                    $"<h1>OAuth failed</h1><p>{HtmlEncode(ex.Message)}</p>");
            }
        });

        // --- /v1/files (Phase 8) ------------------------------------------
        // Loopback download endpoint referenced by TransitFileStore's
        // downloadUrl. Provider actions handing this URL to their upstream
        // API (send-file-by-URL flows) hit the daemon back and get the
        // stored bytes.
        var transit = parentServices.GetService<TransitFileStore>();
        app.MapGet("/v1/files/{fileId}", async (HttpContext ctx, string fileId) =>
        {
            if (transit is null) { await NotConfigured(ctx); return; }
            if (!transit.TryRead(fileId, out var bytes, out var name, out var mime))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await WriteJson(ctx, new JsonObject { ["error"] = "file not found", ["code"] = "RUNTIME_NOT_FOUND" });
                return;
            }
            ctx.Response.ContentType = mime;
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{name.Replace('"', '_')}\"";
            await ctx.Response.Body.WriteAsync(bytes);
        });

        app.MapPost("/v1/files", async (HttpContext ctx) =>
        {
            if (transit is null) { await NotConfigured(ctx); return; }
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            var bytes = ms.ToArray();
            var name = ctx.Request.Query["name"].ToString();
            if (string.IsNullOrEmpty(name)) name = "upload.bin";
            var mime = ctx.Request.ContentType ?? "application/octet-stream";
            var meta = transit.Create(bytes, name, mime);
            await WriteJson(ctx, meta);
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
        // On macOS `.app` bundles baseDir is Contents/MonoBundle/, and the
        // Xamarin publisher moves everything Linked as "Resources/..."
        // one level up to Contents/Resources/. Because our Link prefix
        // is already "Resources/connector/web/", the final path becomes
        // Contents/Resources/Resources/connector/web/ — hence the
        // ../Resources/Resources probe. Same probe pattern OpenCliRuntime
        // uses for its clis dir.
        var baseDir = AppContext.BaseDirectory;
        foreach (var probe in new[]
                 {
                     Path.Combine(baseDir, "Resources", "connector", "web"),
                     Path.Combine(baseDir, "..", "Resources", "connector", "web"),
                     Path.Combine(baseDir, "..", "Resources", "Resources", "connector", "web"),
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

    private static string DefaultRedirectUri(HttpContext ctx)
    {
        // Everywhere daemon binds loopback only; use the request's host.
        var host = ctx.Request.Host.Value ?? "127.0.0.1:7878";
        return $"http://{host}/api/oauth/callback";
    }

    private static Task WriteHtml(HttpContext ctx, int status, string body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        var page =
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>Everywhere Connector</title>" +
            "<style>body{font-family:system-ui,-apple-system,sans-serif;padding:2rem;max-width:40rem;" +
            "margin:auto;color:#222}h1{margin-top:0}code{background:#f5f5f5;padding:2px 6px;border-radius:4px}</style>" +
            "</head><body>" + body + "</body></html>";
        return ctx.Response.WriteAsync(page);
    }

    private static string HtmlEncode(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");
}
