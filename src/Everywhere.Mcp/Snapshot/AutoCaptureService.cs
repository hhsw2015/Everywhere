using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Mcp.Input;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp.Snapshot;

/// <summary>
/// Auto-refresh the context stash whenever the user signals interest in
/// something — text selection, clipboard copy, or pinned element. Replaces
/// the "press SnapshotContext first" friction so a terminal AI prompt picks
/// up the freshest pointer for free.
///
/// Gated on <see cref="McpServerSettings.AutoCaptureContext"/>: when the
/// toggle flips, we (de)register the underlying observers/timer. Stash file
/// lifetime + Take semantics are unchanged — this service only speeds up
/// the *write* side; the hook still consumes once and the file expires
/// after 5 minutes.
/// </summary>
public sealed class AutoCaptureService : IAsyncInitializer, IDisposable
{
    private static readonly TimeSpan SelectionDebounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ClipboardPoll = TimeSpan.FromMilliseconds(500);

    private readonly Settings _settings;
    private readonly ContextStashWriter _writer;
    private readonly IVisualElementContext _context;
    private readonly PickStash _pickStash;
    private readonly IClipboardReader _clipboard;
    private readonly ILogger<AutoCaptureService> _logger;
    private readonly Lock _gate = new();

    private SelectionObserver? _selectionObserver;
    private IDisposable? _selectionSubscription;
    private Action<IVisualElement>? _pickHandler;
    private Timer? _clipboardTimer;
    private string? _lastClipboard;
    private DateTimeOffset _lastSelectionTrigger;
    private int _running;

    public AutoCaptureService(
        Settings settings,
        ContextStashWriter writer,
        IVisualElementContext context,
        PickStash pickStash,
        IClipboardReader clipboard,
        ILogger<AutoCaptureService> logger)
    {
        _settings = settings;
        _writer = writer;
        _context = context;
        _pickStash = pickStash;
        _clipboard = clipboard;
        _logger = logger;
    }

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    public Task InitializeAsync()
    {
        Apply(_settings.McpServer.AutoCaptureContext);
        _settings.McpServer.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(McpServerSettings.AutoCaptureContext))
            {
                Apply(_settings.McpServer.AutoCaptureContext);
            }
        };
        return Task.CompletedTask;
    }

    private void Apply(bool enabled)
    {
        using var _ = _gate.EnterScope();
        if (enabled) Start();
        else Stop();
    }

    private void Start()
    {
        if (_selectionObserver is null)
        {
            _selectionObserver = new SelectionObserver(this);
            try { _selectionSubscription = _context.Subscribe(_selectionObserver); }
            catch (Exception ex) { _logger.LogWarning(ex, "AutoCapture: selection subscribe failed"); }
        }

        if (_pickHandler is null)
        {
            _pickHandler = _ => TryCapture("pick");
            _pickStash.Pinned += _pickHandler;
        }

        if (_clipboardTimer is null)
        {
            _lastClipboard = SafeReadClipboard();
            _clipboardTimer = new Timer(OnClipboardTick, null, ClipboardPoll, ClipboardPoll);
        }
    }

    private void Stop()
    {
        if (_selectionSubscription is { } sub)
        {
            try { sub.Dispose(); } catch { }
            _selectionSubscription = null;
        }
        _selectionObserver = null;

        if (_pickHandler is { } h)
        {
            _pickStash.Pinned -= h;
            _pickHandler = null;
        }

        if (_clipboardTimer is { } t)
        {
            try { t.Dispose(); } catch { }
            _clipboardTimer = null;
            _lastClipboard = null;
        }
    }

    private void OnClipboardTick(object? state)
    {
        try
        {
            var current = _clipboard.GetText();
            if (current is null || current.Length == 0) return;
            if (string.Equals(current, _lastClipboard, StringComparison.Ordinal)) return;
            _lastClipboard = current;
            TryCapture("clipboard");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AutoCapture clipboard tick failed");
        }
    }

    private string? SafeReadClipboard()
    {
        try { return _clipboard.GetText(); }
        catch { return null; }
    }

    private void OnSelection(TextSelectionData data)
    {
        if (string.IsNullOrEmpty(data.Text)) return;
        var now = DateTimeOffset.UtcNow;
        if (now - _lastSelectionTrigger < SelectionDebounce) return;
        _lastSelectionTrigger = now;
        TryCapture("selection");
    }

    private void TryCapture(string reason)
    {
        // Single-flight at this layer too: rapid-fire selection bursts must not
        // pile up dispatcher posts that all serialise on the writer's lock.
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await _writer.CaptureAsync();
                _logger.LogDebug("Auto-captured context ({Reason})", reason);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Auto-capture failed ({Reason})", reason);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }

    public void Dispose()
    {
        using var _ = _gate.EnterScope();
        Stop();
    }

    private sealed class SelectionObserver(AutoCaptureService owner) : IObserver<TextSelectionData>
    {
        public void OnNext(TextSelectionData value) => owner.OnSelection(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
