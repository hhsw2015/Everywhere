using Everywhere.Interop;

namespace Everywhere.Mcp.Snapshot;

/// <summary>
/// OS-wide selection cache. Subscribes to <see cref="IVisualElementContext"/>'s
/// <see cref="TextSelectionData"/> stream and remembers the last non-empty selection
/// for a TTL window. Lets <c>get_selected_text</c> answer "what did the user just
/// select" even after focus moved to a different app — the macOS / Windows / Linux
/// platform context populates the stream as the user highlights text anywhere.
/// </summary>
public sealed class SelectionCache : IObserver<TextSelectionData>, IDisposable
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private readonly IDisposable _subscription;
    private string? _text;
    private string? _appKey;
    private DateTimeOffset _capturedAtUtc;

    public SelectionCache(IVisualElementContext context) : this(context, TimeProvider.System) { }

    public SelectionCache(IVisualElementContext context, TimeProvider clock)
    {
        _clock = clock;
        _subscription = context.Subscribe(this);
    }

    public (string Text, string? AppKey)? GetFresh()
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(_text)) return null;
            if (_clock.GetUtcNow() - _capturedAtUtc > Ttl) return null;
            return (_text, _appKey);
        }
    }

    void IObserver<TextSelectionData>.OnNext(TextSelectionData value)
    {
        if (string.IsNullOrEmpty(value.Text)) return;
        // Resolve appKey OUTSIDE the lock — Process.GetProcessById on Windows is a
        // syscall and we don't want it serialised against GetFresh callers.
        var appKey = value.Element is { } el ? AppKey.FromProcessId(el.ProcessId) : null;
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            _text = value.Text;
            _appKey = appKey;
            _capturedAtUtc = now;
        }
    }

    void IObserver<TextSelectionData>.OnError(Exception error) { }
    void IObserver<TextSelectionData>.OnCompleted() { }

    private int _disposed;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription.Dispose(); } catch { }
    }
}
