using Avalonia;

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

    /// <summary>
    /// Raised the moment the user releases the drag rectangle, BEFORE the
    /// link harvest finishes. Carries just the rect so the overlay host
    /// can paint outline + ➕ immediately. <see cref="Harvested"/> still
    /// fires later with the full link list for stash drain.
    /// </summary>
    public event Action<PixelRect>? RectCommitted;

    public event Action? Cleared;

    /// <summary>
    /// Stash a placeholder rect ahead of the link harvest. Raises
    /// <see cref="RectCommitted"/>. Intended for the new rect-first /
    /// links-later flow — <see cref="AppendLinks"/> updates the entry
    /// once the AX scan completes.
    /// </summary>
    public void SetRect(PixelRect rect, TimeSpan? ttl = null)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            throw new ArgumentException("Empty rect", nameof(rect));
        var effectiveTtl = ttl ?? DefaultTtl;
        if (effectiveTtl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be positive.");
        var expiry = _clock.GetUtcNow() + effectiveTtl;
        bool fireClearedFirst;
        lock (_gate)
        {
            fireClearedFirst = _current is not null;
            _current = new Entry(Array.Empty<HarvestedLink>(), expiry, rect);
        }
        if (fireClearedFirst) Cleared?.Invoke();
        RectCommitted?.Invoke(rect);
    }

    /// <summary>
    /// Replace the link list of the existing entry (kept by SetRect).
    /// Raises <see cref="Harvested"/>. No-op if the slot is empty or has
    /// expired — the harvest result simply doesn't reach the agent, which
    /// is the correct outcome (user already moved on).
    /// </summary>
    public void AppendLinks(IReadOnlyList<HarvestedLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        IReadOnlyList<HarvestedLink>? snapshot = null;
        lock (_gate)
        {
            if (_current is null) return;
            if (_current.ExpiresAtUtc <= _clock.GetUtcNow()) { _current = null; return; }
            _current = _current with { Links = links };
            snapshot = links;
        }
        if (snapshot is not null) Harvested?.Invoke(snapshot);
    }

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

    private sealed record Entry(
        IReadOnlyList<HarvestedLink> Links,
        DateTimeOffset ExpiresAtUtc,
        PixelRect? Rect = null);
}
