using System.Collections.Concurrent;

namespace Everywhere.Mcp.OpenCli.Observation;

/// <summary>
/// SPEC §Phase 1 store. In-memory only; server restart invalidates
/// (§Phase 1 limits — SESSION_NOT_FOUND on cold read). LRU by
/// LastAccessed; 60min idle TTL swept lazily on read/write.
/// </summary>
public sealed class CaptureSessionStore
{
    public const int MaxConcurrent = 10;
    public const int MaxRequestsPerSession = 500;
    public const long MaxBodyBytesPerSession = 64L * 1024 * 1024;
    public const int MaxCaptureDurationMs = 10 * 60 * 1000;
    public const long IdleTtlMs = 60L * 60 * 1000;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly IClock _clock;

    public CaptureSessionStore(IClock? clock = null)
    {
        _clock = clock ?? SystemClock.Instance;
    }

    private sealed class Entry
    {
        public required CaptureSession Session { get; init; }
        public long LastAccessed { get; set; }
        public long TotalBodyBytes { get; set; }
        public int OversizeBodyDrops { get; set; }
    }

    public CaptureSession Start(int tabId, string origin)
    {
        Sweep();
        if (_entries.Count >= MaxConcurrent)
            throw new CaptureLimitException(MaxConcurrent, _entries.Count);

        var now = _clock.NowMs();
        var session = new CaptureSession
        {
            SessionId = Guid.NewGuid().ToString("D"),
            TabId = tabId,
            Origin = origin,
            StartedAt = now,
        };
        _entries[session.SessionId] = new Entry { Session = session, LastAccessed = now };
        return session;
    }

    public CaptureSession Get(string sessionId)
    {
        if (!_entries.TryGetValue(sessionId, out var e))
            throw new SessionNotFoundException(sessionId);
        var now = _clock.NowMs();
        if (now - e.LastAccessed > IdleTtlMs)
        {
            _entries.TryRemove(sessionId, out _);
            throw new SessionExpiredException(sessionId, "idle_ttl");
        }
        if (e.Session.StoppedAt is null && now - e.Session.StartedAt > MaxCaptureDurationMs)
        {
            e.Session.StoppedAt = now;
            throw new SessionExpiredException(sessionId, "max_capture_duration");
        }
        e.LastAccessed = now;
        return e.Session;
    }

    public CaptureSession Stop(string sessionId)
    {
        var s = Get(sessionId);
        if (s.StoppedAt is null) s.StoppedAt = _clock.NowMs();
        return s;
    }

    /// <summary>Append a request; enforces caps. Returns false when session full.</summary>
    public bool AppendRequest(string sessionId, CaptureSession.NetworkRequest req, string? bodyContent = null)
    {
        var e = GetEntry(sessionId);
        lock (e)
        {
            if (e.Session.Network.Requests.Count >= MaxRequestsPerSession) return false;
            // A single body larger than the whole per-session budget is dropped
            // — the request itself is still recorded (via sha256 + size) so the
            // Verdict scorer classifies it deterministically instead of silently
            // treating an absent body as noise.
            if (bodyContent is not null && bodyContent.Length > MaxBodyBytesPerSession)
            {
                e.Session.Network.Requests.Add(req);
                e.OversizeBodyDrops++;
                return true;
            }
            e.Session.Network.Requests.Add(req);
            if (bodyContent is not null && !string.IsNullOrEmpty(req.ResponseBodySha256))
            {
                var newBytes = bodyContent.Length;
                while (e.TotalBodyBytes + newBytes > MaxBodyBytesPerSession && e.Session.Network.Requests.Count > 1)
                {
                    // Drop oldest body — spec: "drop oldest on overflow"
                    var oldest = e.Session.Network.Requests
                        .Select(r => r.ResponseBodySha256)
                        .FirstOrDefault(h => !string.IsNullOrEmpty(h) && e.Session.Network.BodiesByHash.ContainsKey(h));
                    if (oldest is null) break;
                    if (e.Session.Network.BodiesByHash.Remove(oldest, out var dropped))
                        e.TotalBodyBytes -= dropped.Length;
                }
                if (e.TotalBodyBytes + newBytes <= MaxBodyBytesPerSession
                    && !e.Session.Network.BodiesByHash.ContainsKey(req.ResponseBodySha256))
                {
                    e.Session.Network.BodiesByHash[req.ResponseBodySha256] = bodyContent;
                    e.TotalBodyBytes += newBytes;
                }
            }
            return true;
        }
    }

    /// <summary>Test-visible count of bodies dropped for oversize.</summary>
    public int OversizeBodyDropCount(string sessionId)
        => GetEntry(sessionId).OversizeBodyDrops;

    public void AppendConsole(string sessionId, CaptureSession.ConsoleMessage msg)
    {
        var e = GetEntry(sessionId);
        lock (e) e.Session.Console.Messages.Add(msg);
    }

    public void AppendMutation(string sessionId, CaptureSession.DomMutation m)
    {
        var e = GetEntry(sessionId);
        lock (e) e.Session.DomMutations.Add(m);
    }

    public void AppendGesture(string sessionId, CaptureSession.UserGesture g)
    {
        var e = GetEntry(sessionId);
        lock (e) e.Session.UserGestures.Add(g);
    }

    public void AppendSignature(string sessionId, CaptureSession.SignatureSample s)
    {
        var e = GetEntry(sessionId);
        lock (e) e.Session.Signatures.Add(s);
    }

    public int ActiveCount => _entries.Count;

    private Entry GetEntry(string sessionId)
    {
        if (!_entries.TryGetValue(sessionId, out var e))
            throw new SessionNotFoundException(sessionId);
        e.LastAccessed = _clock.NowMs();
        return e;
    }

    private void Sweep()
    {
        var now = _clock.NowMs();
        foreach (var kv in _entries)
            if (now - kv.Value.LastAccessed > IdleTtlMs)
                _entries.TryRemove(kv.Key, out _);
    }
}

public interface IClock { long NowMs(); }

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public sealed class FakeClock(long start = 0) : IClock
{
    private long _now = start;
    public long NowMs() => _now;
    public void Advance(TimeSpan by) => _now += (long)by.TotalMilliseconds;
    public void SetNowMs(long ms) => _now = ms;
}

public sealed class SessionNotFoundException(string id)
    : Exception($"SESSION_NOT_FOUND: {id}") { public string SessionId { get; } = id; }

public sealed class SessionExpiredException(string id, string reason)
    : Exception($"SESSION_EXPIRED: {id} ({reason})")
{
    public string SessionId { get; } = id;
    public string Reason { get; } = reason;
}

public sealed class CaptureLimitException(int max, int current)
    : Exception($"CAPTURE_LIMIT_EXCEEDED: max={max} current={current}")
{
    public int Max { get; } = max;
    public int Current { get; } = current;
}
