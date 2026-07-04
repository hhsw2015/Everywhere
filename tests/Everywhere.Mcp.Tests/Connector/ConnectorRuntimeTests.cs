// SPEC docs/specs/everywhere-connector.md §10 — Phase-1 verification.
//
// Runs entirely against the vendored open-connector bundle. Manifest
// tests don't touch V8 (cheap). Smoke tests boot the isolate and exercise
// one no-credential path + one credential-required path.

using System.Net.Http;
using System.Text.Json.Nodes;
using Everywhere.Mcp.Connector;

namespace Everywhere.Mcp.Tests.Connector;

[TestFixture]
public class ConnectorRuntimeTests
{
    private static string FindBundleDir()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "3rd", "open-connector", "dist", "connector.bundle.js")))
            dir = dir.Parent;
        if (dir is null)
            throw new DirectoryNotFoundException("3rd/open-connector/dist/connector.bundle.js not found above test directory");
        return Path.Combine(dir.FullName, "3rd", "open-connector", "dist");
    }

    private sealed class NullResolver : ICredentialResolver
    {
        public JsonObject? Resolve(string service) => null;
    }

    private sealed class StaticResolver : ICredentialResolver
    {
        private readonly string _service;
        private readonly string _apiKey;
        public StaticResolver(string service, string apiKey) { _service = service; _apiKey = apiKey; }
        public JsonObject? Resolve(string service)
        {
            if (!string.Equals(service, _service, StringComparison.OrdinalIgnoreCase)) return null;
            return new JsonObject
            {
                ["authType"] = "api_key",
                ["apiKey"] = _apiKey,
                ["values"] = new JsonObject { ["apiKey"] = _apiKey },
                ["profile"] = new JsonObject
                {
                    ["accountId"] = "test",
                    ["displayName"] = "test account",
                    ["grantedScopes"] = new JsonArray(),
                },
                ["metadata"] = new JsonObject(),
            };
        }
    }

    [Test]
    public void ManifestLoadsGithubProvider()
    {
        var bundleDir = FindBundleDir();
        using var http = new HttpClient();
        var runtime = new ConnectorRuntime(bundleDir, http, new NullResolver());

        var manifest = runtime.ListManifest();
        Assert.That(manifest.Services, Is.Not.Empty, "manifest must contain at least one service");

        var github = manifest.Services.FirstOrDefault(s => s.Service == "github");
        Assert.That(github, Is.Not.Null, "github service must be present in manifest");
        Assert.That(github!.Actions, Is.Not.Empty, "github must expose actions");
        Assert.That(github.Actions.Any(a => a.Name == "get_current_user"),
            Is.True, "github.get_current_user action must be listed");
    }

    [Test]
    public void ManifestCoversCoreProviders()
    {
        // Phase 7 sanity — the bundle must include the ~60 providers
        // pinned in the allowlist. If an upstream file rename or a
        // scripts/build-connector-bundle.mjs typo drops one, this test
        // fires before we ship a manifest with a hole in it.
        var bundleDir = FindBundleDir();
        using var http = new HttpClient();
        var runtime = new ConnectorRuntime(bundleDir, http, new NullResolver());
        var services = runtime.ListManifest().Services.Select(s => s.Service).ToHashSet();
        foreach (var expected in new[] { "github", "hackernews", "openai", "anthropic",
                                          "linear", "gitlab", "asana", "airtable",
                                          "algolia", "discord", "dropbox", "figma",
                                          "gemini", "firecrawl" })
        {
            Assert.That(services, Contains.Item(expected), $"provider '{expected}' missing from bundle");
        }
    }

    [Test]
    public async Task InvokeWithoutCredentialsReturnsAuthorizationFailed()
    {
        var bundleDir = FindBundleDir();
        using var http = new HttpClient();
        await using var runtime = new ConnectorRuntime(bundleDir, http, new NullResolver());

        var result = await runtime.InvokeAsync("github", "get_current_user", new JsonObject());

        Assert.That(result["ok"]!.GetValue<bool>(), Is.False,
            "no credentials should surface as ok=false");
        Assert.That(result["code"]!.GetValue<string>(), Is.EqualTo("authorization_failed"),
            "upstream 401 should map to authorization_failed");
        Assert.That(result["hint"], Is.Not.Null, "authorization_failed should carry a hint");
    }

    [Test]
    public async Task InvokeUnknownServiceReturnsRuntimeNotFound()
    {
        var bundleDir = FindBundleDir();
        using var http = new HttpClient();
        await using var runtime = new ConnectorRuntime(bundleDir, http, new NullResolver());

        var result = await runtime.InvokeAsync("nonexistent_provider_xyz", "some_action", new JsonObject());

        Assert.That(result["ok"]!.GetValue<bool>(), Is.False);
        Assert.That(result["code"]!.GetValue<string>(), Is.EqualTo("RUNTIME_NOT_FOUND"));
    }

    /// <summary>
    /// Live smoke test. Skipped unless EVERYWHERE_CONNECTOR_GITHUB_PAT is
    /// set in the environment. In CI you can enable it by adding a
    /// GITHUB_PAT_TEST secret and exporting it into this env var.
    /// Category=ConnectorSmoke so it's filterable.
    /// </summary>
    [Test]
    [Category("ConnectorSmoke")]
    public async Task GetCurrentUser_LiveHitsGitHub()
    {
        var pat = Environment.GetEnvironmentVariable("EVERYWHERE_CONNECTOR_GITHUB_PAT");
        if (string.IsNullOrEmpty(pat))
        {
            Assert.Ignore("EVERYWHERE_CONNECTOR_GITHUB_PAT not set — skipping live smoke test");
            return;
        }

        var bundleDir = FindBundleDir();
        using var http = new HttpClient();
        await using var runtime = new ConnectorRuntime(bundleDir, http, new StaticResolver("github", pat));

        var result = await runtime.InvokeAsync("github", "get_current_user", new JsonObject());

        Assert.That(result["ok"]!.GetValue<bool>(), Is.True,
            $"expected ok=true, got: {result.ToJsonString()}");
        var data = result["data"] as JsonObject;
        Assert.That(data, Is.Not.Null, "success envelope must include data");
        Assert.That(data!["login"], Is.Not.Null, "GitHub API response must include login");
    }
}
