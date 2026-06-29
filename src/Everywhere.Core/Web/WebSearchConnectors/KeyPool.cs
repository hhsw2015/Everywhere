using System.Collections.Concurrent;

namespace Everywhere.Web;

/// <summary>
/// Rotating API-key pool with rate-limit cooldowns.
///
/// Ported from CLIProxyAPIPlus's wsKeyPool (Go,
/// internal/runtime/executor/websearch_keypool.go). One pool per provider.
/// <see cref="Next"/> returns keys round-robin, skipping any that the
/// connector has marked rate-limited within the last 60 seconds. If
/// every key is in cooldown we still return one — the connector will
/// see the 429 again and the caller decides what to do.
///
/// Thread-safe: cursor is atomic, cooldown map is concurrent.
/// </summary>
public sealed class KeyPool
{
    private readonly IReadOnlyList<string> _keys;
    private readonly ConcurrentDictionary<string, DateTime> _cooldown = new(StringComparer.Ordinal);
    private long _cursor;

    public KeyPool(IEnumerable<string> keys)
    {
        _keys = keys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public int Count => _keys.Count;

    public bool IsEmpty => _keys.Count == 0;

    /// <summary>
    /// Pick the next available key. Returns null only when the pool is empty.
    /// Walks the full ring once: if every key is in cooldown we fall back
    /// to the cursor's natural slot (caller will see another 429 and can
    /// surface a clearer error).
    /// </summary>
    public string? Next()
    {
        if (_keys.Count == 0) return null;
        var now = DateTime.UtcNow;
        var n = _keys.Count;
        var start = Interlocked.Increment(ref _cursor) - 1;
        for (var i = 0; i < n; i++)
        {
            var idx = (int)(((start + i) % n + n) % n);
            var key = _keys[idx];
            if (_cooldown.TryGetValue(key, out var until))
            {
                if (now < until) continue;
                _cooldown.TryRemove(key, out _);
            }
            return key;
        }
        return _keys[(int)((start % n + n) % n)];
    }

    /// <summary>
    /// Mark a key rate-limited for 60 seconds. Connectors call this on
    /// 429 / 401-with-quota / similar — pool will route around it.
    /// </summary>
    public void MarkRateLimited(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        _cooldown[key] = DateTime.UtcNow.AddSeconds(60);
    }
}
