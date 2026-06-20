using Avalonia.Input;
using Everywhere.Interop;
using Lucide.Avalonia;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public sealed partial class ShortcutSettings : SettingsBase, ISettingsCategory
{
    [SettingsItemIgnore]
    public int Index => 2;

    [SettingsItemIgnore]
    public LucideIconKind Icon => LucideIconKind.Keyboard;

    [SettingsItemIgnore]
    public IDynamicResourceKey TitleKey { get; } = new DynamicResourceKey(LocaleKey.SettingsCategory_Settings_Shortcut_Header);

    [SettingsItemIgnore]
    public IDynamicResourceKey? DescriptionKey { get; } = new DynamicResourceKey(LocaleKey.SettingsCategory_Settings_Shortcut_Description);

    [DynamicResourceKey(
        LocaleKey.ShortcutSettings_ChatWindow_Header,
        LocaleKey.ShortcutSettings_ChatWindow_Desription)]
    [SettingsTemplatedItem]
    public CompositeKeyboardShortcut ChatWindow { get; set; } = new KeyboardShortcut(Key.E, KeyModifiers.Control | KeyModifiers.Shift);

    [DynamicResourceKey(
        LocaleKey.ShortcutSettings_PickVisualElement_Header,
        LocaleKey.ShortcutSettings_PickVisualElement_Desription)]
    [SettingsTemplatedItem]
    public CompositeKeyboardShortcut PickVisualElement { get; } = new();

    [DynamicResourceKey(
        LocaleKey.ShortcutSettings_TakeScreenshot_Header,
        LocaleKey.ShortcutSettings_TakeScreenshot_Desription)]
    [SettingsTemplatedItem]
    public CompositeKeyboardShortcut TakeScreenshot { get; } = new();

    /// <summary>
    /// Lets the user "pin" a UI element for AI agents (MCP read_pick) without opening the chat window.
    /// </summary>
    [DynamicResourceKey(
        LocaleKey.ShortcutSettings_AgentPickElement_Header,
        LocaleKey.ShortcutSettings_AgentPickElement_Desription)]
    [SettingsTemplatedItem]
    public CompositeKeyboardShortcut AgentPickElement { get; } = new();

    /// <summary>
    /// Captures a context pointer (focused app + window title + URL + selection)
    /// to a per-OS stash file so a subsequent Claude Code UserPromptSubmit hook
    /// can inject it into the next agent prompt. Tree text and screenshots are
    /// intentionally NOT captured — the agent calls the corresponding MCP tools
    /// (get_focused_context / screenshot) on demand.
    /// </summary>
    [DynamicResourceKey(
        LocaleKey.ShortcutSettings_SnapshotContext_Header,
        LocaleKey.ShortcutSettings_SnapshotContext_Desription)]
    [SettingsTemplatedItem]
    public CompositeKeyboardShortcut SnapshotContext { get; } = new();

    /// <summary>
    /// Manual escape hatch. Wipes the on-disk context stash and any fresh
    /// pin so the next prompt to a Claude Code hook is NOT decorated. Use
    /// when you accidentally captured something you don't want injected.
    /// </summary>
    [DynamicResourceKey(
        LocaleKey.ShortcutSettings_ClearContextStash_Header,
        LocaleKey.ShortcutSettings_ClearContextStash_Desription)]
    [SettingsTemplatedItem]
    public CompositeKeyboardShortcut ClearContextStash { get; } = new();

    /// <summary>
    /// Press-and-hold to draw on a virtual whiteboard over the current screen:
    /// hold the hotkey, draw any number of strokes (circle / underline / arrow / X)
    /// on the content you want to highlight, release to snap each gesture to
    /// real a11y leaves and hand the regions to the agent.
    /// </summary>
    [DynamicResourceKey(
        LocaleKey.ShortcutSettings_Whiteboard_Header,
        LocaleKey.ShortcutSettings_Whiteboard_Desription)]
    [SettingsTemplatedItem]
    public CompositeKeyboardShortcut Whiteboard { get; } = new();
}