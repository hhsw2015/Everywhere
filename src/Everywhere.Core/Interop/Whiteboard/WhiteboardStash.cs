namespace Everywhere.Interop.Whiteboard;

/// <summary>
/// Cross-call buffer holding the regions a user just drew on the whiteboard.
/// Mirrors <see cref="PickStash"/> semantics: Take() consumes the slot so
/// the next agent call sees an empty stash, matching the "I drew this for
/// that one question" mental model.
///
/// Replacing an unread session is fine — the new whiteboard wins (user
/// changed their mind before the agent read it).
/// </summary>
public sealed class WhiteboardStash
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private Entry? _current;

    public WhiteboardStash() : this(TimeProvider.System) { }

    public WhiteboardStash(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>
    /// Raised after a successful <see cref="Set"/>. Lets ContextStashWriter
    /// react to a fresh whiteboard without polling.
    /// </summary>
    public event Action<IReadOnlyList<WhiteboardRegion>>? Drawn;

    public void Set(IReadOnlyList<WhiteboardRegion> regions, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(regions);
        if (regions.Count == 0)
            throw new ArgumentException("At least one region required", nameof(regions));
        var expiry = _clock.GetUtcNow() + (ttl ?? DefaultTtl);
        lock (_gate)
        {
            _current = new Entry(regions, expiry);
        }
        Drawn?.Invoke(regions);
    }

    /// <summary>
    /// Atomically reads and clears the slot. Returns null if empty or expired.
    /// </summary>
    public IReadOnlyList<WhiteboardRegion>? Take()
    {
        lock (_gate)
        {
            var entry = _current;
            _current = null;
            if (entry is null) return null;
            return entry.ExpiresAtUtc <= _clock.GetUtcNow() ? null : entry.Regions;
        }
    }

    /// <summary>
    /// Snapshots without consuming. Used for diagnostics / status indicators.
    /// </summary>
    public IReadOnlyList<WhiteboardRegion>? Peek()
    {
        lock (_gate)
        {
            if (_current is null) return null;
            return _current.ExpiresAtUtc <= _clock.GetUtcNow() ? null : _current.Regions;
        }
    }

    public bool HasFreshWhiteboard
    {
        get
        {
            lock (_gate)
            {
                return _current is { } e && e.ExpiresAtUtc > _clock.GetUtcNow();
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _current = null;
        }
    }

    private sealed record Entry(
        IReadOnlyList<WhiteboardRegion> Regions,
        DateTimeOffset ExpiresAtUtc);
}
