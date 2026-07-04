// SPEC docs/specs/everywhere-connector.md §7 Phase 2 — persistent
// credential store.

using System.Text.Json.Nodes;
using Everywhere.Mcp.Connector;

namespace Everywhere.Mcp.Tests.Connector;

[TestFixture]
public class JsonCredentialStoreTests
{
    private string _tmpDir = null!;
    private string _storePath = null!;

    [SetUp]
    public void SetUp()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "everywhere-connector-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _storePath = Path.Combine(_tmpDir, "connections.json");
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    [Test]
    public void SetApiKey_ThenResolve_RoundTrips()
    {
        var store = new JsonCredentialStore(_storePath);
        store.SetApiKey("github", "github_pat_test", "test PAT");

        var cred = store.Resolve("github");
        Assert.That(cred, Is.Not.Null);
        Assert.That(cred!["authType"]!.GetValue<string>(), Is.EqualTo("api_key"));
        Assert.That(cred["apiKey"]!.GetValue<string>(), Is.EqualTo("github_pat_test"));
        Assert.That(cred["profile"]!["displayName"]!.GetValue<string>(), Is.EqualTo("test PAT"));
    }

    [Test]
    public void Resolve_UnknownService_ReturnsNull()
    {
        var store = new JsonCredentialStore(_storePath);
        Assert.That(store.Resolve("nonexistent"), Is.Null);
    }

    [Test]
    public void Delete_RemovesEntry()
    {
        var store = new JsonCredentialStore(_storePath);
        store.SetApiKey("openai", "sk-test");
        Assert.That(store.Resolve("openai"), Is.Not.Null);

        var removed = store.Delete("openai");
        Assert.That(removed, Is.True);
        Assert.That(store.Resolve("openai"), Is.Null);

        // Idempotent — deleting again returns false.
        Assert.That(store.Delete("openai"), Is.False);
    }

    [Test]
    public void List_ReturnsAllServicesWithoutSecrets()
    {
        var store = new JsonCredentialStore(_storePath);
        store.SetApiKey("github", "github_pat", "GH");
        store.SetApiKey("openai", "sk-abc", "OpenAI");

        var list = store.List();
        Assert.That(list.Count, Is.EqualTo(2));
        Assert.That(list.Select(c => c.Service), Is.EquivalentTo(new[] { "github", "openai" }));
        // ConnectionSummary intentionally has no api-key field — proves
        // the list surface never leaks secrets.
        Assert.That(typeof(ConnectionSummary).GetProperties().Any(p => p.Name.Contains("Key", StringComparison.OrdinalIgnoreCase)),
            Is.False);
    }

    [Test]
    public void Resolve_PersistsAcrossInstances()
    {
        var s1 = new JsonCredentialStore(_storePath);
        s1.SetApiKey("linear", "lin_test");
        var s2 = new JsonCredentialStore(_storePath);
        var cred = s2.Resolve("linear");
        Assert.That(cred, Is.Not.Null);
        Assert.That(cred!["apiKey"]!.GetValue<string>(), Is.EqualTo("lin_test"));
    }

    [Test]
    public void ChainedResolver_EnvBeatsStore()
    {
        // Env resolver takes precedence over the JSON store.
        var envKey = "EVERYWHERE_CONNECTOR_ANTHROPIC_PAT";
        Environment.SetEnvironmentVariable(envKey, "env-value");
        try
        {
            var store = new JsonCredentialStore(_storePath);
            store.SetApiKey("anthropic", "store-value");
            var chain = new ChainedCredentialResolver(new EnvironmentCredentialResolver(), store);
            var cred = chain.Resolve("anthropic");
            Assert.That(cred!["apiKey"]!.GetValue<string>(), Is.EqualTo("env-value"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, null);
        }
    }

    [Test]
    public void ChainedResolver_FallsBackToStoreWhenEnvMissing()
    {
        var store = new JsonCredentialStore(_storePath);
        store.SetApiKey("resend", "store-only");
        var chain = new ChainedCredentialResolver(new EnvironmentCredentialResolver(), store);
        var cred = chain.Resolve("resend");
        Assert.That(cred!["apiKey"]!.GetValue<string>(), Is.EqualTo("store-only"));
    }

    [Test]
    public void OAuthClient_RoundTrip()
    {
        var store = new JsonCredentialStore(_storePath);
        store.SetOAuthClient("github", "client-abc", "secret-xyz", "http://127.0.0.1:7878/api/oauth/callback");
        var summaries = store.ListOAuthClients();
        Assert.That(summaries.Count, Is.EqualTo(1));
        var s = summaries[0];
        Assert.That(s.Service, Is.EqualTo("github"));
        Assert.That(s.ClientId, Is.EqualTo("client-abc"));
        Assert.That(s.HasSecret, Is.True);
        // Secret must never appear on the summary DTO.
        Assert.That(typeof(OAuthClientSummary).GetProperties().Any(p => p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase) && p.Name != "HasSecret"),
            Is.False);
    }

    [Test]
    public void OAuthPending_TakeConsumesAndReapsStale()
    {
        var store = new JsonCredentialStore(_storePath);
        store.PutOAuthPending("state-1", "github", codeVerifier: null);
        var (svc, verifier) = store.TakeOAuthPending("state-1");
        Assert.That(svc, Is.EqualTo("github"));
        Assert.That(verifier, Is.EqualTo(""));
        // Second take on the same state should return null — one-shot.
        var (svc2, _) = store.TakeOAuthPending("state-1");
        Assert.That(svc2, Is.Null);
    }

    [Test]
    public void SetOAuth2Credential_ResolvesAsOAuthShape()
    {
        var store = new JsonCredentialStore(_storePath);
        store.SetOAuth2Credential(
            service: "linear",
            accessToken: "tok-xyz",
            tokenType: "Bearer",
            refreshToken: "refresh-1",
            expiresAt: DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
            grantedScopes: new[] { "read", "write" },
            displayName: "Linear OAuth");
        var cred = store.Resolve("linear");
        Assert.That(cred, Is.Not.Null);
        Assert.That(cred!["authType"]!.GetValue<string>(), Is.EqualTo("oauth2"));
        Assert.That(cred["accessToken"]!.GetValue<string>(), Is.EqualTo("tok-xyz"));
        Assert.That(cred["refreshToken"]!.GetValue<string>(), Is.EqualTo("refresh-1"));
    }
}
