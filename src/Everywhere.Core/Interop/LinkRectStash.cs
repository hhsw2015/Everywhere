namespace Everywhere.Interop;

/// <summary>
/// Cross-call buffer holding the links a user just rect-harvested. Mirrors
/// <see cref="PickStash"/> / <see cref="Whiteboard.WhiteboardStash"/>
/// semantics: Take() consumes the slot so a subsequent SnapshotContext
/// sees an empty stash. Set raises <see cref="Harvested"/>; Clear raises
/// <see cref="Cleared"/> so the overlay host can tear down its outline +
/// ➕ visuals on the user's wipe hotkey.
///
/// Per the v0.9.183 UX-consistency pass, LinkRect no longer ships
/// immediately on hotkey release — the harvest lands here, the user
/// optionally annotates via the ➕, then SnapshotContext drains and
/// ships. Same model as Pin / Whiteboard.
/// </summary>
public sealed class LinkRectStash
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private Entry? _current;

    public LinkRectStash() : this(TimeProvider.System) { }

    public LinkRectStash(TimeProvider clock)
    {
        _clock = clock;
    }

    public event Action<IReadOnlyList<HarvestedLink>>? Harvested;
    public event Action? Cleared;

    public void Set(IReadOnlyList<HarvestedLink> links, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(links);
        if (links.Count == 0)
            throw new ArgumentException("At least one link required", nameof(links));
        var effectiveTtl = ttl ?? DefaultTtl;
        if (effectiveTtl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be positive; non-positive would create a dead-on-arrival entry that fires Harvested then immediately expires.");
        var expiry = _clock.GetUtcNow() + effectiveTtl;
        bool fireClearedFirst;
        lock (_gate)
        {
            // Replace semantics: if there was a live entry, raise Cleared
            // BEFORE Harvested so subscribers can teardown the prior
            // overlay before showing the new one — and they don't have
            // to special-case "Harvested implicitly clears".
            fireClearedFirst = _current is not null;
            _current = new Entry(links, expiry);
        }
        if (fireClearedFirst) Cleared?.Invoke();
        Harvested?.Invoke(links);
    }

    public IReadOnlyList<HarvestedLink>? Peek()
    {
        bool fireCleared;
        IReadOnlyList<HarvestedLink>? result;
        lock (_gate)
        {
            if (_current is null) return null;
            if (_current.ExpiresAtUtc <= _clock.GetUtcNow())
            {
                _current = null;
                fireCleared = true;
                result = null;
            }
            else
            {
                fireCleared = false;
                result = _current.Links;
            }
        }
        // Fire OUTSIDE the lock so a subscriber can call back in safely.
        // Mirrors PickStash.Take(): TTL-expiry MUST raise Cleared so
        // overlay hosts tear down stale visuals instead of leaving a ➕
        // floating with no backing stash entry.
        if (fireCleared) Cleared?.Invoke();
        return result;
    }

    public IReadOnlyList<HarvestedLink>? Take()
    {
        bool fireCleared;
        IReadOnlyList<HarvestedLink>? result;
        lock (_gate)
        {
            if (_current is null) return null;
            var entry = _current;
            _current = null;
            if (entry.ExpiresAtUtc <= _clock.GetUtcNow())
            {
                fireCleared = true;
                result = null;
            }
            else
            {
                fireCleared = false;
                result = entry.Links;
            }
        }
        if (fireCleared) Cleared?.Invoke();
        return result;
    }

    public bool HasFreshHarvest
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
        lock (_gate) { _current = null; }
    }

    public void ClearWithEvent()
    {
        bool fire;
        lock (_gate)
        {
            fire = _current is not null;
            _current = null;
        }
        if (fire) Cleared?.Invoke();
    }

    private sealed record Entry(IReadOnlyList<HarvestedLink> Links, DateTimeOffset ExpiresAtUtc);
}
