using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Lucide.Avalonia;

namespace Everywhere.Configuration;

/// <summary>
/// One entry in <see cref="McpServerSettings.KnownApps"/>: a window-title
/// regex paired with a discovery URL. When the user is on a window whose
/// title matches the regex, the context-stash writer appends a hint
/// pointing the agent at the discovery URL — typically a
/// <c>/.well-known/agent-skills</c> endpoint that lists the app's
/// SKILL.md files. Keeps Everywhere ignorant of any specific app.
/// </summary>
public sealed partial class KnownApp : ObservableObject
{
    [ObservableProperty] public partial string TitlePattern { get; set; } = string.Empty;
    [ObservableProperty] public partial string DiscoverUrl { get; set; } = string.Empty;
}

[GeneratedSettingsItems]
public sealed partial class McpServerSettings(IServiceProvider serviceProvider) : SettingsBase(serviceProvider), ISettingsCategory
{
    [SettingsItemIgnore]
    public int Index => 9;

    [SettingsItemIgnore]
    public LucideIconKind Icon => LucideIconKind.Plug;

    [SettingsItemIgnore]
    public IDynamicLocaleKey TitleKey { get; } = new DynamicLocaleKey(LocaleKey.SettingsCategory_Settings_McpServer_Header);

    [SettingsItemIgnore]
    public IDynamicLocaleKey? DescriptionKey { get; } = new DynamicLocaleKey(LocaleKey.SettingsCategory_Settings_McpServer_Description);

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.McpServerSettings_HttpEnabled_Header,
        LocaleKey.McpServerSettings_HttpEnabled_Description)]
    public partial bool HttpEnabled { get; set; } = true;

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.McpServerSettings_HttpPort_Header,
        LocaleKey.McpServerSettings_HttpPort_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(HttpEnabled), Group = "_")]
    [SettingsIntegerItem(Min = 1, Max = 65535, IsSliderVisible = false)]
    public partial int HttpPort { get; set; } = 7878;

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.McpServerSettings_AutoCaptureContext_Header,
        LocaleKey.McpServerSettings_AutoCaptureContext_Description)]
    public partial bool AutoCaptureContext { get; set; } = false;

    /// <summary>
    /// Native opendia browser bridge. When enabled, Everywhere stands up a
    /// loopback websocket server on <see cref="OpenDiaPort"/> that the
    /// unmodified opendia Chrome / Firefox extension connects to. The
    /// extension's full tool surface (page_extract_content, element_click,
    /// tab_list, get_history, ...) becomes available to any MCP client
    /// pointed at Everywhere — no node.js / npm dependency.
    /// </summary>
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.McpServerSettings_OpenDiaEnabled_Header,
        LocaleKey.McpServerSettings_OpenDiaEnabled_Description)]
    public partial bool OpenDiaEnabled { get; set; } = false;

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.McpServerSettings_OpenDiaPort_Header,
        LocaleKey.McpServerSettings_OpenDiaPort_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(OpenDiaEnabled), Group = "_")]
    [SettingsIntegerItem(Min = 1, Max = 65535, IsSliderVisible = false)]
    public partial int OpenDiaPort { get; set; } = 5555;

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.McpServerSettings_AgentAppId_Header,
        LocaleKey.McpServerSettings_AgentAppId_Description)]
    [SettingsStringItem(PlaceholderText = "com.github.cmux")]
    public partial string AgentAppId { get; set; } = string.Empty;

    /// <summary>
    /// Optional kick-off phrase typed into the agent app + Enter after
    /// Everywhere has captured user-picked context (LinkRect harvest, pick,
    /// whiteboard region) AND raised the agent app to the front. Only fires
    /// when this is non-empty AND the capture actually produced data —
    /// blank phrase means "raise the agent and let me type".
    /// </summary>
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.McpServerSettings_LaunchPhrase_Header,
        LocaleKey.McpServerSettings_LaunchPhrase_Description)]
    [SettingsStringItem(PlaceholderText = "take a look")]
    public partial string LaunchPhrase { get; set; } = string.Empty;

    /// <summary>
    /// User-registered local web apps that self-describe their agent skills
    /// at a /.well-known/-style URL. ContextStashWriter matches the active
    /// window title against each entry's regex; on hit, it injects a hint
    /// pointing the agent at the discovery URL. Add entries here when you
    /// install a tool that hosts agent-skills discovery — Everywhere itself
    /// stays ignorant of any specific app.
    /// </summary>
    [ObservableProperty]
    [SettingsItemIgnore]
    public partial ObservableCollection<KnownApp> KnownApps { get; set; } = [];

    /// <summary>
    /// Software cursor overlay: historical .NET port of OCCU's overlay.
    /// Hidden from settings UI — on macOS the OCCU Swift bridge owns
    /// the cursor overlay (libAxHelper.dylib renders its own NSPanel
    /// during ax_click / ax_scroll / ax_drag), so the .NET overlay
    /// would conflict. On Windows / Linux the MCP automation tools
    /// don't fire IInputSimulator, so the overlay would never animate.
    /// The persisted value is still honoured for diagnostic builds
    /// that flip it via settings.json directly.
    /// </summary>
    [ObservableProperty]
    [SettingsItemIgnore]
    [DynamicLocaleKey(
        LocaleKey.McpServerSettings_CursorOverlayEnabled_Header,
        LocaleKey.McpServerSettings_CursorOverlayEnabled_Description)]
    public partial bool CursorOverlayEnabled { get; set; } = false;
}
