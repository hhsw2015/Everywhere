using System.Text.Json.Nodes;
using Microsoft.ClearScript;

namespace Everywhere.Mcp.OpenCli;

/// <summary>
/// SPEC §4.4 — the subset of <c>cli({...})</c> registration metadata
/// that the host stores. <c>func</c> is kept as an opaque
/// <see cref="ScriptObject"/> handle for re-entry into the V8 isolate;
/// <c>pipeline</c> is captured as a serialised <see cref="JsonNode"/>
/// (the pipeline runner is forbidden by SPEC §2.4 #1, so we never
/// re-execute it — keep it for diagnostics / surface only).
///
/// Implemented as a <see langword="class"/> rather than a <see langword="record"/>
/// because <c>ScriptObject</c> / <see cref="JsonArray"/> / <see cref="JsonNode"/>
/// members compare by reference, so synthesised value-equality on a record would
/// be meaningless and surprise anything that puts AdapterDef in a HashSet/dict.
/// We override <see cref="Equals"/>/<see cref="GetHashCode"/> on
/// <see cref="FullName"/> — the only stable identity.
/// </summary>
public sealed class AdapterDef : IEquatable<AdapterDef>
{
    public AdapterDef(
        string site, string name, string description, string strategy, bool browser,
        string? access, string? domain, IReadOnlyList<string>? aliases,
        JsonArray? args, JsonArray? columns, ScriptObject? func, JsonNode? pipeline,
        string? navigateBefore = null)
    {
        if (string.IsNullOrEmpty(site)) throw new ArgumentException("site required", nameof(site));
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("name required", nameof(name));
        if (site.Contains('/')) throw new ArgumentException("site must not contain '/' — would collide with FullName routing key", nameof(site));
        if (name.Contains('/')) throw new ArgumentException("name must not contain '/' — would collide with FullName routing key", nameof(name));
        Site = site; Name = name;
        // Normalize null → empty so ToDescribeJson never emits a JSON
        // null where SPEC §4.2 requires a string.
        Description = description ?? string.Empty;
        Strategy = strategy ?? string.Empty;
        Browser = browser;
        Access = access; Domain = domain; Aliases = aliases;
        Args = args; Columns = columns; Func = func; Pipeline = pipeline;
        NavigateBefore = navigateBefore;
        FullName = site + "/" + name;
    }

    public string Site { get; }
    public string Name { get; }
    public string Description { get; }
    public string Strategy { get; }
    public bool Browser { get; }
    public string? Access { get; }
    public string? Domain { get; }
    public IReadOnlyList<string>? Aliases { get; }
    /// <summary>navigateBefore URL from manifest. Null when the manifest
    /// sets false / unset — adapter targets whatever tab is active.
    /// Non-null means the runtime pre-navigates before invoking the
    /// adapter body — safe to route through a background tab.</summary>
    public string? NavigateBefore { get; }
    public JsonArray? Args { get; }
    public JsonArray? Columns { get; }
    public ScriptObject? Func { get; }
    public JsonNode? Pipeline { get; }

    public string FullName { get; }

    /// <summary>
    /// SPEC docs/specs/everywhere-self-expanding.md §10.1 — where the
    /// adapter came from. <c>"vendored"</c> (the default) means shipped
    /// under <c>3rd/opencli/clis/</c>; <c>"local"</c> means loaded from
    /// <c>~/.everywhere/adapters/&lt;site&gt;/</c>. The Restricted HostShim
    /// (§6) uses this to scope <c>fs</c>/<c>fetch</c>/<c>page.cdp</c>.
    /// </summary>
    public string Origin { get; init; } = "vendored";

    public bool Equals(AdapterDef? other) => other is not null && other.FullName == FullName;
    public override bool Equals(object? obj) => Equals(obj as AdapterDef);
    public override int GetHashCode() => FullName.GetHashCode(StringComparison.Ordinal);

    /// <summary>SPEC §4.2 — describe envelope.</summary>
    public JsonObject ToDescribeJson()
    {
        var o = new JsonObject
        {
            ["schema_version"] = "1",
            ["site"] = Site,
            ["name"] = Name,
            ["description"] = Description,
            ["strategy"] = Strategy,
            ["browser"] = Browser,
            ["args"] = Args?.DeepClone() ?? new JsonArray(),
            ["columns"] = Columns?.DeepClone() ?? new JsonArray(),
        };
        if (Access is not null) o["access"] = Access;
        if (Domain is not null) o["domain"] = Domain;
        AppendAliases(o);
        return o;
    }

    /// <summary>SPEC §4.1 — one element of the <c>commands</c> array.</summary>
    public JsonObject ToListEntry()
    {
        var o = new JsonObject
        {
            ["site"] = Site,
            ["name"] = Name,
            ["description"] = Description,
            ["strategy"] = Strategy,
            ["browser"] = Browser,
            ["args"] = Args?.DeepClone() ?? new JsonArray(),
        };
        AppendAliases(o);
        return o;
    }

    private void AppendAliases(JsonObject o)
    {
        if (Aliases is not { Count: > 0 }) return;
        var clean = new List<JsonNode?>(Aliases.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in Aliases)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var trimmed = raw.Trim();
            if (seen.Add(trimmed)) clean.Add(trimmed);
        }
        if (clean.Count > 0) o["aliases"] = new JsonArray(clean.ToArray());
    }
}
