using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Everywhere.Mcp.OpenCli.Memory;

/// <summary>SPEC §Phase 3 schemas — EndpointSpec, FieldMapEntry, StrategyNote, VerifyFixture.</summary>
public sealed class EndpointSpec
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("method")] public string Method { get; init; } = "GET";
    [JsonPropertyName("url_template")] public string UrlTemplate { get; init; } = "";
    [JsonPropertyName("request_headers")] public Dictionary<string, string> RequestHeaders { get; init; } = new();
    [JsonPropertyName("response_content_type")] public string ResponseContentType { get; init; } = "";
    [JsonPropertyName("strategy")] public string Strategy { get; init; } = "public";
    [JsonPropertyName("cookies_required")] public List<string> CookiesRequired { get; init; } = [];
    [JsonPropertyName("signature_scheme")] public string? SignatureScheme { get; init; }
    [JsonPropertyName("parameter_map")] public Dictionary<string, JsonNode?> ParameterMap { get; init; } = new();
    [JsonPropertyName("verified_at")] public long VerifiedAt { get; init; }
    [JsonPropertyName("mutation")] public bool Mutation { get; init; }
}

public sealed class FieldMapEntry
{
    [JsonPropertyName("stable_name")] public string StableName { get; init; } = "";
    [JsonPropertyName("decoder")] public string? Decoder { get; init; }
    [JsonPropertyName("sample_value")] public JsonNode? SampleValue { get; init; }
    [JsonPropertyName("confidence")] public double Confidence { get; init; }
}

public sealed class StrategyNote
{
    [JsonPropertyName("strategy")] public string Strategy { get; init; } = "public";
    [JsonPropertyName("contract")] public string Contract { get; init; } = "stable";
    [JsonPropertyName("evidence")] public List<string> Evidence { get; init; } = [];
    [JsonPropertyName("replay")] public string Replay { get; init; } = "";
    [JsonPropertyName("mutation")] public bool Mutation { get; init; }
    [JsonPropertyName("created_at")] public long CreatedAt { get; init; }

    public bool IsComplete(out List<string> missing)
    {
        missing = [];
        if (Evidence.Count < 3 || Evidence.Any(e => (e ?? "").Length < 20)) missing.Add("evidence");
        if ((Replay ?? "").Length < 50) missing.Add("replay");
        if (Strategy is not ("public" or "cookie" or "intercept" or "ui")) missing.Add("strategy");
        if (Contract is not ("stable" or "visible-ui" or "internal-unstable")) missing.Add("contract");
        return missing.Count == 0;
    }
}

public sealed class VerifyFixture
{
    [JsonPropertyName("cmd")] public string Cmd { get; init; } = "";
    [JsonPropertyName("args")] public Dictionary<string, JsonNode?> Args { get; init; } = new();
    [JsonPropertyName("patterns")] public Dictionary<string, string> Patterns { get; init; } = new();
    [JsonPropertyName("notEmpty")] public List<string> NotEmpty { get; init; } = [];
    [JsonPropertyName("mustNotContain")] public Dictionary<string, List<string>> MustNotContain { get; init; } = new();
    [JsonPropertyName("mustBeTruthy")] public List<string> MustBeTruthy { get; init; } = [];
    [JsonPropertyName("expected_row_count_min")] public int ExpectedRowCountMin { get; init; }
    [JsonPropertyName("expected_row_count_max")] public int ExpectedRowCountMax { get; init; }

    public bool Is4TupleComplete(out List<string> missing)
    {
        missing = [];
        if (Patterns.Count < 1) missing.Add("patterns");
        if (NotEmpty.Count < 1) missing.Add("notEmpty");
        if (MustNotContain.Count < 1) missing.Add("mustNotContain");
        if (MustBeTruthy.Count < 1) missing.Add("mustBeTruthy");
        return missing.Count == 0;
    }
}

/// <summary>SPEC §Phase 3 metadata.json envelope.</summary>
public sealed class SiteMetadata
{
    [JsonPropertyName("verified_at")] public long VerifiedAt { get; set; }
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("adapter_versions")] public Dictionary<string, int> AdapterVersions { get; init; } = new();
}
