using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.OpenDia;

/// <summary>
/// Mirrors the live tool list the opendia extension has registered into the
/// running MCP server's <see cref="McpServerOptions.ToolCollection"/>.
///
/// Triggered on bridge state-change: when the extension (re)connects we
/// drop any stale tools we previously added and add fresh ones; on
/// disconnect we drop everything we own. Built-in Everywhere tools are
/// untouched.
/// </summary>
public sealed class OpenDiaToolSync
{
    private const string Prefix = "browser_";

    private static string EnsurePrefix(string name) =>
        // Guard against opendia tools that already happen to start with
        // `browser_` (or a future opendia release renaming them) so the
        // exposed MCP name never becomes `browser_browser_x`.
        name.StartsWith(Prefix, StringComparison.Ordinal) ? name : Prefix + name;
    private readonly OpenDiaBridge _bridge;
    private readonly IOptions<McpServerOptions> _options;
    private readonly ILogger<OpenDiaToolSync> _logger;
    private readonly object _gate = new();
    private readonly List<McpServerTool> _ownedTools = new();
    private bool _wired;

    public OpenDiaToolSync(
        OpenDiaBridge bridge,
        IOptions<McpServerOptions> options,
        ILogger<OpenDiaToolSync> logger)
    {
        _bridge = bridge;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Subscribe to bridge state changes and do an initial sync. Idempotent
    /// — host startup paths can call multiple times.
    /// </summary>
    public void Wire()
    {
        lock (_gate)
        {
            if (_wired) return;
            _wired = true;
        }
        _bridge.StateChanged += SyncSafe;
        SyncSafe();
    }

    private void SyncSafe()
    {
        try { Sync(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenDia: tool sync failed");
        }
    }

    private void Sync()
    {
        var tools = _options.Value.ToolCollection;
        if (tools is null)
        {
            _logger.LogDebug("OpenDia: McpServerOptions.ToolCollection is null; skipping sync.");
            return;
        }

        lock (_gate)
        {
            // Drop tools we previously added — they may be stale specs from
            // a previous extension session.
            foreach (var t in _ownedTools)
            {
                try { tools.Remove(t); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "OpenDia: failed to remove stale tool {Name}", t.ProtocolTool.Name);
                }
            }
            _ownedTools.Clear();

            foreach (var spec in _bridge.AvailableTools)
            {
                var tool = TryBuildTool(spec);
                if (tool is null) continue;
                // TryAdd: silently no-ops on a name collision so we can't
                // double-register if a built-in static tool ever takes the
                // same `browser_*` slot.
                if (tools.TryAdd(tool))
                    _ownedTools.Add(tool);
                else
                    _logger.LogDebug(
                        "OpenDia: tool {Name} already present, skipping",
                        tool.ProtocolTool.Name);
            }
            _logger.LogInformation(
                "OpenDia: synced {Count} browser tools into MCP collection.",
                _ownedTools.Count);
        }
    }

    private McpServerTool? TryBuildTool(JsonObject spec)
    {
        var origName = spec["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(origName)) return null;
        var description = spec["description"]?.GetValue<string>() ?? string.Empty;
        var schema = spec["inputSchema"] as JsonObject ?? new JsonObject { ["type"] = "object" };

        JsonElement schemaElement;
        try
        {
            // Deserialize<JsonElement> doesn't keep the JsonDocument alive
            // (no pooled buffer rented for the lifetime of the result),
            // unlike RootElement.Clone() which leaves the parent doc on
            // the GC heap until finalization.
            schemaElement = JsonSerializer.Deserialize<JsonElement>(schema.ToJsonString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "OpenDia: invalid inputSchema for tool {Name}; defaulting to empty object",
                origName);
            schemaElement = JsonSerializer.Deserialize<JsonElement>("""{"type":"object"}""");
        }

        var mcpName = EnsurePrefix(origName!);
        var fn = new OpenDiaAIFunction(_bridge, mcpName, origName!, description, schemaElement);
        return McpServerTool.Create(fn);
    }
}
