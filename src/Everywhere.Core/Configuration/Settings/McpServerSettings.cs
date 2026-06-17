using CommunityToolkit.Mvvm.ComponentModel;
using Lucide.Avalonia;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public sealed partial class McpServerSettings : SettingsBase, ISettingsCategory
{
    [SettingsItemIgnore]
    public int Index => 9;

    [SettingsItemIgnore]
    public LucideIconKind Icon => LucideIconKind.Plug;

    [SettingsItemIgnore]
    public IDynamicResourceKey TitleKey { get; } = new DynamicResourceKey(LocaleKey.SettingsCategory_Settings_McpServer_Header);

    [SettingsItemIgnore]
    public IDynamicResourceKey? DescriptionKey { get; } = new DynamicResourceKey(LocaleKey.SettingsCategory_Settings_McpServer_Description);

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.McpServerSettings_HttpEnabled_Header,
        LocaleKey.McpServerSettings_HttpEnabled_Description)]
    public partial bool HttpEnabled { get; set; } = true;

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.McpServerSettings_HttpPort_Header,
        LocaleKey.McpServerSettings_HttpPort_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(HttpEnabled), Group = "_")]
    [SettingsIntegerItem(Min = 1, Max = 65535, IsSliderVisible = false)]
    public partial int HttpPort { get; set; } = 7878;

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.McpServerSettings_AutoCaptureContext_Header,
        LocaleKey.McpServerSettings_AutoCaptureContext_Description)]
    public partial bool AutoCaptureContext { get; set; } = false;
}
