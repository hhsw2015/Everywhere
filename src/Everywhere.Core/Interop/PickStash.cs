namespace Everywhere.Interop;

/// <summary>
/// Single-slot cross-call buffer holding the element a user just "pinned" for an AI agent
/// via the Agent Pick hotkey. The MCP <c>read_pick</c> tool consumes the slot (Take semantics)
/// so the next call sees an empty stash again, mirroring the user mental model
/// "I pinned this for that one question".
///
/// Pins expire after <see cref="DefaultTtl"/> so a forgotten pin doesn't leak into a future
/// agent conversation. Replacing an unread pin is fine — the new one wins.
/// </summary>
public sealed class PickStash
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private Entry? _current;

    public PickStash() : this(TimeProvider.System) { }

    public PickStash(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>
    /// Raised after a successful <see cref="Set"/>. Lets background services
    /// (e.g. context stash auto-capture) react to a fresh pin without polling.
    /// Handlers run synchronously on the caller thread; keep them lightweight.
    /// </summary>
    public event Action<IVisualElement>? Pinned;

    /// <summary>
    /// Raised when the slot transitions from filled to empty (Take, Clear,
    /// or a TTL-expired read). Used by the annotation overlay so the
    /// floating ➕ badge can hide once the pin no longer applies.
    /// Handlers run synchronously on the caller thread.
    /// </summary>
    public event Action? Cleared;

    public void Set(IVisualElement element, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        var expiry = _clock.GetUtcNow() + (ttl ?? DefaultTtl);
        lock (_gate)
        {
            _current = new Entry(element, expiry);
        }
        Pinned?.Invoke(element);
    }

    /// <summary>
    /// Atomically reads and clears the slot. Returns null if empty or expired.
    /// </summary>
    public IVisualElement? Take()
    {
        IVisualElement? result;
        bool fireCleared;
        lock (_gate)
        {
            var entry = _current;
            fireCleared = entry is not null;
            _current = null;
            if (entry is null) result = null;
            else result = entry.ExpiresAtUtc <= _clock.GetUtcNow() ? null : entry.Element;
        }
        if (fireCleared) Cleared?.Invoke();
        return result;
    }

    /// <summary>
    /// Snapshots without consuming. Used for diagnostics / status indicators in the UI.
    /// </summary>
    public bool HasFreshPin
    {
        get
        {
            lock (_gate)
            {
                return _current is { } e && e.ExpiresAtUtc > _clock.GetUtcNow();
            }
        }
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

    public void Clear()
    {
        lock (_gate)
        {
            _current = null;
        }
    }

    private sealed record Entry(IVisualElement Element, DateTimeOffset ExpiresAtUtc);
}
