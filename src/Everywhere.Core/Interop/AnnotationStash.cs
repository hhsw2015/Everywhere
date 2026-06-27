using System.Collections.Immutable;

namespace Everywhere.Interop;

/// <summary>
/// What kind of perception channel the annotation is anchored to. Lets
/// the LLM tell at a glance whether the user pointed (pin), framed
/// (whiteboard), highlighted (selected), or harvested (linkrect).
/// </summary>
public enum AnnotationSource
{
    Pin,
    Whiteboard,
    Selected,
    LinkRect,
}

/// <summary>
/// One annotation: a user note attached to a perception anchor.
/// </summary>
/// <param name="Source">Which perception channel produced this anchor.</param>
/// <param name="Body">The free-form note the user typed (or dictated).</param>
/// <param name="AnchorRef">
/// Opaque identifier the source-specific stash can use to look the anchor
/// back up (e.g. element_index for pin, region id for whiteboard). May be
/// null when the source uses the latest-only model (e.g. selected text is
/// stamped at capture time).
/// </param>
/// <param name="AnchorLabel">
/// Short human-readable description of what the annotation is attached
/// to, displayed to the LLM verbatim (e.g. "AXButton \"Submit\"",
/// "region 210x110 circle in Notes"). Resolved at <see cref="AnnotationStash.Add"/>
/// time and frozen — we do NOT re-resolve later, so the label survives even
/// if the underlying source stash expires.
/// </param>
/// <param name="CapturedAtUtc">UTC time the annotation was authored.</param>
public sealed record AnnotationItem(
    AnnotationSource Source,
    string Body,
    string? AnchorRef,
    string AnchorLabel,
    DateTimeOffset CapturedAtUtc);

/// <summary>
/// Multi-entry append-and-drain buffer holding user annotations queued
/// for the next SnapshotContext send. Unlike <see cref="PickStash"/>
/// which holds a single slot the agent Takes, the annotation flow is
/// "user accumulates N notes across pins/whiteboard/selection, then
/// ships them all in one shot". Drained on each SnapshotContext capture.
///
/// TTL exists so a long-forgotten note doesn't leak into a future
/// conversation if the user never sends. Each add bumps its own expiry
/// independently; expired entries are silently dropped on the next read.
/// </summary>
public sealed class AnnotationStash
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

    // Defensive caps so a buggy or malicious MCP client can't grow the
    // queue / a single entry without bound. The hard limits sit at the
    // single choke point that both the MCP path and any future UI path
    // funnel through.
    public const int MaxBodyLength = 8_000;
    public const int MaxAnchorLabelLength = 400;
    public const int MaxAnchorRefLength = 200;
    public const int MaxQueueDepth = 200;

    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private readonly List<Entry> _entries = new();

    public AnnotationStash() : this(TimeProvider.System) { }

    public AnnotationStash(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>
    /// Raised after a successful <see cref="Add"/>. Lets the UI float
    /// the tray when there's at least one queued annotation.
    /// </summary>
    public event Action<AnnotationItem>? Added;

    /// <summary>
    /// Raised when an annotation is removed via <see cref="Remove"/> or
    /// the stash is drained. Lets the UI hide the tray when empty.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Queue an annotation. Returns the post-insert live count (atomic
    /// with the insert — never observes a concurrent drain happening
    /// between insert and read). Rejects oversize fields and overflow
    /// of the queue depth cap by throwing <see cref="ArgumentException"/>.
    /// </summary>
    public int Add(AnnotationItem item, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Body.Length > MaxBodyLength)
            throw new ArgumentException($"body exceeds {MaxBodyLength} characters", nameof(item));
        if (item.AnchorLabel.Length > MaxAnchorLabelLength)
            throw new ArgumentException($"anchor_label exceeds {MaxAnchorLabelLength} characters", nameof(item));
        if (item.AnchorRef is { Length: > MaxAnchorRefLength })
            throw new ArgumentException($"anchor_ref exceeds {MaxAnchorRefLength} characters", nameof(item));

        var expiry = _clock.GetUtcNow() + (ttl ?? DefaultTtl);
        int newCount;
        lock (_gate)
        {
            // Prune first so the queue-depth check matches what callers
            // will actually see in Peek/Drain.
            PruneExpired(_clock.GetUtcNow());
            if (_entries.Count >= MaxQueueDepth)
                throw new ArgumentException($"queue depth would exceed {MaxQueueDepth}", nameof(item));
            _entries.Add(new Entry(item, expiry));
            newCount = _entries.Count;
        }
        // Fire Changed first so state-aware subscribers see the new size
        // before the per-item handler runs. Wrap subscribers in try/finally
        // so an exception in Added still surfaces Changed and never leaves
        // the UI tray out of sync with the stash.
        try { Changed?.Invoke(); }
        finally { Added?.Invoke(item); }
        return newCount;
    }

    /// <summary>
    /// Read the live (non-expired) entries without consuming them. The
    /// list is materialised so callers can iterate without holding the
    /// lock.
    /// </summary>
    public ImmutableArray<AnnotationItem> Peek()
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            PruneExpired(now);
            if (_entries.Count == 0) return ImmutableArray<AnnotationItem>.Empty;
            var builder = ImmutableArray.CreateBuilder<AnnotationItem>(_entries.Count);
            foreach (var e in _entries) builder.Add(e.Item);
            return builder.ToImmutable();
        }
    }

    /// <summary>
    /// Atomically read all live entries and clear the stash. Called by
    /// the SnapshotContext capture path so a single send consumes
    /// everything queued so far.
    /// </summary>
    public ImmutableArray<AnnotationItem> Drain()
    {
        var now = _clock.GetUtcNow();
        ImmutableArray<AnnotationItem> result;
        bool hadEntries;
        lock (_gate)
        {
            PruneExpired(now);
            if (_entries.Count == 0) return ImmutableArray<AnnotationItem>.Empty;
            var builder = ImmutableArray.CreateBuilder<AnnotationItem>(_entries.Count);
            foreach (var e in _entries) builder.Add(e.Item);
            result = builder.ToImmutable();
            _entries.Clear();
            hadEntries = true;
        }
        if (hadEntries) Changed?.Invoke();
        return result;
    }

    /// <summary>
    /// Remove the first live entry whose item is the SAME reference as
    /// <paramref name="item"/> (reference identity, not value equality —
    /// two records with identical fields will NOT match). Used by the
    /// annotation overlay to replace a previously-committed note: the
    /// caller passes back the exact instance it received from a prior
    /// Add, we drop it, then Add a new one, keeping the stash free of
    /// orphan revisions.
    /// </summary>
    public bool RemoveItem(AnnotationItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        bool removed;
        lock (_gate)
        {
            PruneExpired(_clock.GetUtcNow());
            var idx = _entries.FindIndex(e => ReferenceEquals(e.Item, item));
            if (idx < 0) return false;
            _entries.RemoveAt(idx);
            removed = true;
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    public bool Remove(int index)
    {
        bool removed;
        lock (_gate)
        {
            // Prune before bounds-checking so the caller's index lines up
            // with the same view Peek would have returned.
            PruneExpired(_clock.GetUtcNow());
            if (index < 0 || index >= _entries.Count) return false;
            _entries.RemoveAt(index);
            removed = true;
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <summary>
    /// Drop exactly the items that were returned by a prior Peek. Used by
    /// the snapshot pipeline so an annotation is only consumed AFTER the
    /// on-disk ctx file is written successfully — a transient I/O failure
    /// leaves the queued notes available for the user's next press. Items
    /// are matched by reference identity against the underlying entries;
    /// anything that has since been added or expired stays.
    /// </summary>
    public void Consume(ImmutableArray<AnnotationItem> matched)
    {
        if (matched.IsDefaultOrEmpty) return;
        lock (_gate)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                if (matched.Contains(_entries[i].Item))
                {
                    _entries.RemoveAt(i);
                }
            }
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        bool hadEntries;
        lock (_gate)
        {
            hadEntries = _entries.Count > 0;
            _entries.Clear();
        }
        if (hadEntries) Changed?.Invoke();
    }

    public int Count
    {
        get
        {
            var now = _clock.GetUtcNow();
            lock (_gate)
            {
                PruneExpired(now);
                return _entries.Count;
            }
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        // Caller holds _gate.
        _entries.RemoveAll(e => e.ExpiresAt <= now);
    }

    private readonly record struct Entry(AnnotationItem Item, DateTimeOffset ExpiresAt);
}
