using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using Everywhere.Utilities;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp;

/// <summary>
/// Wires the ClearContextStash hotkey. Lets the user wipe a misfired stash
/// (e.g. SnapshotContext caught the wrong window) before the next prompt
/// reaches Claude Code, so they don't get an unwanted [everywhere-ctx]
/// injection. Mirrors <see cref="SnapshotContextHotkeyInitializer"/>'s
/// register/dispose dance for IsEnabled / Main / Alternative changes.
/// </summary>
public sealed class ClearContextStashHotkeyInitializer : IAsyncInitializer
{
    private readonly Settings _settings;
    private readonly IShortcutListener _shortcutListener;
    private readonly ContextStashWriter _writer;
    private readonly ILogger<ClearContextStashHotkeyInitializer> _logger;
    private readonly Lock _syncLock = new();

    public ClearContextStashHotkeyInitializer(
        Settings settings,
        IShortcutListener shortcutListener,
        ContextStashWriter writer,
        ILogger<ClearContextStashHotkeyInitializer> logger)
    {
        _settings = settings;
        _shortcutListener = shortcutListener;
        _writer = writer;
        _logger = logger;
    }

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    public Task InitializeAsync()
    {
        InitializeShortcut(_settings.Shortcut.ClearContextStash);
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
            slot = _shortcutListener.Register(shortcut, OnPressed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register ClearContextStash shortcut {Shortcut}", shortcut);
        }
    }

    private void OnPressed()
    {
        Dispatcher.UIThread.Post(() =>
        {
            try { _writer.ClearStash(); }
            catch (Exception ex) { _logger.LogWarning(ex, "ClearContextStash hotkey handler failed"); }
        });
    }
}
