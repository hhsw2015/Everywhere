using System.Collections.Concurrent;

namespace Everywhere.Mcp.Meta;

/// <summary>
/// SPEC §Phase 6 — per-HTTP-session active domain set. Keyed by MCP
/// session id (or HttpContext.Connection.Id when running behind Kestrel).
/// Disconnect clears the entry, so a reconnect returns to the default
/// <c>search</c> tier. Uses <see cref="ConcurrentDictionary"/> both at the
/// outer session level and as a set-of-strings so concurrent MCP calls on
/// the same session don't corrupt or NRE the domain set.
/// </summary>
public sealed class SessionActivations
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _active =
        new(StringComparer.Ordinal);

    private ConcurrentDictionary<string, byte> GetSet(string sessionId)
        => _active.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));

    /// <summary>Snapshot of the domain names currently active for <paramref name="sessionId"/>.</summary>
    public ISet<string> Get(string sessionId)
        => new HashSet<string>(GetSet(sessionId).Keys, StringComparer.Ordinal);

    public bool Activate(string sessionId, string domain)
    {
        // Accept SPEC aliases (e.g. observation → browser_core).
        if (TierGate.DomainAliases.TryGetValue(domain, out var canonical)) domain = canonical;
        if (!TierGate.Domains.ContainsKey(domain) && domain != "full") return false;
        GetSet(sessionId).TryAdd(domain, 1);
        return true;
    }

    public void ResetForDisconnect(string sessionId) => _active.TryRemove(sessionId, out _);

    public bool IsActive(string sessionId, string domain)
    {
        var set = GetSet(sessionId);
        return set.ContainsKey(domain) || set.ContainsKey("full");
    }
}
