using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using Everywhere.Utilities;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp;

/// <summary>
/// Wires the user-configurable SnapshotContext hotkey. When pressed, captures the
/// current focused-app context (URL, selection, a11y summary, screenshot) and writes
/// it to ~/Library/Application Support/Everywhere/context-stash.json so a later
/// Claude Code UserPromptSubmit hook can pick it up.
///
/// Lives in Everywhere.Mcp (not Core) because the writer + selection cache pull on
/// MCP-only types; Core is the wrong place to depend on those.
/// </summary>
public sealed class SnapshotContextHotkeyInitializer : IAsyncInitializer
{
    private readonly Settings _settings;
    private readonly IShortcutListener _shortcutListener;
    private readonly ContextStashWriter _writer;
    private readonly ILogger<SnapshotContextHotkeyInitializer> _logger;
    private readonly Lock _syncLock = new();

    public SnapshotContextHotkeyInitializer(
        Settings settings,
        IShortcutListener shortcutListener,
        ContextStashWriter writer,
        ILogger<SnapshotContextHotkeyInitializer> logger)
    {
        _settings = settings;
        _shortcutListener = shortcutListener;
        _writer = writer;
        _logger = logger;
    }

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    public Task InitializeAsync()
    {
        InitializeShortcut(_settings.Shortcut.SnapshotContext);
        return Task.CompletedTask;
    }

    private void InitializeShortcut(CompositeKeyboardShortcut shortcut)
    {
        IDisposable? mainSubscription = null;
        IDisposable? alternativeSubscription = null;

        shortcut.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(CompositeKeyboardShortcut.IsEnabled):
                {
                    if (shortcut.IsEnabled) RegisterAll();
                    else
                    {
                        using var _0 = _syncLock.EnterScope();
                        DisposeHelper.DisposeToDefault(ref mainSubscription);
                        DisposeHelper.DisposeToDefault(ref alternativeSubscription);
                    }
                    break;
                }
                case nameof(CompositeKeyboardShortcut.Main) when shortcut.IsEnabled:
                    RegisterOne(shortcut.Main, ref mainSubscription);
                    break;
                case nameof(CompositeKeyboardShortcut.Alternative) when shortcut.IsEnabled:
                    RegisterOne(shortcut.Alternative, ref alternativeSubscription);
                    break;
            }
        };

        if (shortcut.IsEnabled) RegisterAll();

        void RegisterAll()
        {
            if (shortcut.Main.IsValid) RegisterOne(shortcut.Main, ref mainSubscription);
            if (shortcut.Alternative.IsValid) RegisterOne(shortcut.Alternative, ref alternativeSubscription);
        }
    }

    private void RegisterOne(KeyboardShortcut shortcut, ref IDisposable? slot)
    {
        using var _0 = _syncLock.EnterScope();
        DisposeHelper.DisposeToDefault(ref slot);
        if (!shortcut.IsValid) return;
        try
        {
            slot = _shortcutListener.Register(shortcut, OnSnapshotPressed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register SnapshotContext shortcut {Shortcut}", shortcut);
        }
    }

    // macOS routes the key-up of a global hotkey to the app that was
    // frontmost when the combo was matched. If we raise the agent app
    // while the OS is still routing that key-up to (e.g.) Arc, the
    // source app re-asserts frontmost. Yield long enough for the
    // modifiers to release before raising. Windows/Linux don't show
    // this race in practice — the cost is paid only where it matters.
    private const int MacosModifierReleaseDelayMs = 180;

    // Tick of the most recently *accepted* hotkey press (not yet
    // necessarily a successful capture). Coalesces rapid repeats from a
    // single press — macOS sometimes delivers the shortcut twice
    // (KeyDown + modifier-release re-fire), and at v0.9.157+ the second
    // call would overwrite the on-disk ctx file with a fresh snapshot
    // that had already drained the user's queued annotations.
    //
    // 1500ms window: observed re-fire gap was ~520ms after subtracting
    // the modifier-release delay. 600ms (v0.9.163) was uncomfortably
    // close; widening to 1500ms gives headroom against GC pauses /
    // dispatcher backlog without being long enough that a user
    // deliberately re-firing notices the suppression.
    private long _lastAcceptedPressTicks;
    private const int RepeatSuppressionMs = 1500;

    private void OnSnapshotPressed()
    {
        var nowTicks = Environment.TickCount64;
        var prev = Interlocked.Read(ref _lastAcceptedPressTicks);
        if (prev != 0 && (nowTicks - prev) < RepeatSuppressionMs)
        {
            _logger.LogDebug("Snapshot hotkey re-fired within {Ms}ms; ignoring", nowTicks - prev);
            return;
        }
        // CompareExchange so two concurrent callers don't both observe
        // the same `prev` and both proceed past the guard. Loser logs
        // and returns. (IShortcutListener dispatch thread is
        // platform-dependent; we don't assume single-threaded delivery.)
        if (Interlocked.CompareExchange(ref _lastAcceptedPressTicks, nowTicks, prev) != prev)
        {
            _logger.LogDebug("Snapshot hotkey lost coalescing race; ignoring");
            return;
        }

        // Capture must happen on the UI thread because IVisualElement.CaptureAsync
        // may bounce through Avalonia rendering. Fire-and-forget; user just wants
        // the hotkey to feel instant. The writer logs failures itself.
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (OperatingSystem.IsMacOS())
                    await Task.Delay(MacosModifierReleaseDelayMs);
                await _writer.CaptureAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Snapshot context hotkey handler failed");
            }
        });
    }
}
