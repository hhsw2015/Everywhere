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
}