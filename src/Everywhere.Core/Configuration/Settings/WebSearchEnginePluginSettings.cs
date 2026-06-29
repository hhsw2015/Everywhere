using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Avalonia.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Collections;
using Everywhere.Views;
using Everywhere.Web;

namespace Everywhere.Configuration;

[TypeConverter(typeof(FallbackEnumConverter))]
public enum WebSearchEngineProviderId
{
    Official,
    AnySearch,
    Bocha,
    Brave,
    Google,
    Jina,
    SearXNG,
    Tavily,
    UniFuncs,
    TinyFish,
}

public interface IWebSearchEngineProvider
{
    WebSearchEngineProviderId Id { get; }

    IDynamicLocaleKey HeaderKey { get; }

    string IconUrl { get; }

    string? DocsUrl { get; }

    SettingsItems SettingsItems { get; }

    bool Validate();
}

[GeneratedSettingsItems]
public sealed partial class OfficialWebSearchEngineSettings : ObservableObject
{
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.OfficialWebSearchEngineProvider_Depth_Header,
        LocaleKey.OfficialWebSearchEngineProvider_Depth_Description)]
    [SettingsItem(Group = "_")]
    public partial OfficialConnector.SearchDepth Depth { get; set; }

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.OfficialWebSearchEngineProvider_Topic_Header,
        LocaleKey.OfficialWebSearchEngineProvider_Topic_Description)]
    [SettingsItem(Group = "_")]
    public partial OfficialConnector.SearchTopic Topic { get; set; }

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.OfficialWebSearchEngineProvider_TimeRange_Header,
        LocaleKey.OfficialWebSearchEngineProvider_TimeRange_Description)]
    [SettingsItem(Group = "_")]
    public partial OfficialConnector.SearchTimeRange TimeRange { get; set; }
}

[GeneratedSettingsItems]
public sealed partial class OfficialWebSearchEngineProvider : ObservableObject, IWebSearchEngineProvider
{
    [JsonIgnore]
    [SettingsItemIgnore]
    public WebSearchEngineProviderId Id => WebSearchEngineProviderId.Official;

    [JsonIgnore]
    [SettingsItemIgnore]
    public IDynamicLocaleKey HeaderKey { get; } = new DynamicLocaleKey(LocaleKey.WebSearchEngineProvider_Official);

    [JsonIgnore]
    [SettingsItemIgnore]
    public string IconUrl => "avares://Everywhere.Core/Assets/Icons/everywhere-rounded.png";

    [JsonIgnore]
    [SettingsItemIgnore]
    public string? DocsUrl => null;

    [SettingsItemIgnore]
    public OfficialWebSearchEngineSettings Settings { get; } = new();

    [DynamicLocaleKey(LocaleKey.Empty)]
    [SettingsItem(Classes = ["Ghost", "NoHeading"])]
    public SettingsControl<OfficialWebSearchProviderSettingsControl> SettingsControl =>
        new(x => new OfficialWebSearchProviderSettingsControl(x, Settings));

    public bool Validate() => true;

    public override bool Equals(object? obj) => obj is IWebSearchEngineProvider provider && Id == provider.Id;

    public override int GetHashCode() => Id.GetHashCode();
}

public abstract class ThirdPartyWebSearchEngineProvider : ObservableValidator, IWebSearchEngineProvider
{
    [JsonIgnore]
    [SettingsItemIgnore]
    public abstract WebSearchEngineProviderId Id { get; }

    [JsonIgnore]
    [SettingsItemIgnore]
    public abstract IDynamicLocaleKey HeaderKey { get; }

    [JsonIgnore]
    [SettingsItemIgnore]
    public abstract string IconUrl { get; }

    [JsonIgnore]
    [SettingsItemIgnore]
    public abstract string? DocsUrl { get; }

    public abstract SettingsItems SettingsItems { get; }

    public bool Validate()
    {
        ValidateAllProperties();
        return !HasErrors;
    }

    public override bool Equals(object? obj) => obj is IWebSearchEngineProvider provider && Id == provider.Id;

    public override int GetHashCode() => Id.GetHashCode();
}

[GeneratedSettingsItems]
public sealed partial class GoogleWebSearchEngineProvider() : ThirdPartyWebSearchEngineProvider
{
    [JsonIgnore]
    [SettingsItemIgnore]
    public override WebSearchEngineProviderId Id => WebSearchEngineProviderId.Google;

    [JsonIgnore]
    [SettingsItemIgnore]
    public override IDynamicLocaleKey HeaderKey { get; } = new DirectLocaleKey("Google");

    [JsonIgnore]
    [SettingsItemIgnore]
    public override string IconUrl => "avares://Everywhere.Core/Assets/Icons/google-color.svg";

    [JsonIgnore]
    [SettingsItemIgnore]
    public override string DocsUrl => "https://developers.google.com/custom-search/v1/overview";

    [DynamicLocaleKey(
        LocaleKey.WebSearchEngineProvider_EndPoint_Header,
        LocaleKey.WebSearchEngineProvider_EndPoint_Description)]
    [SettingsItem(Group = "_")]
    public Customizable<string> EndPoint { get; } = new("https://customsearch.googleapis.com", isDefaultValueReadonly: true);

    [ObservableProperty]
    [SettingsItemIgnore]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(ApiKey), nameof(Configuration.ApiKey.Validate))]
    public partial Guid ApiKey { get; set; }

    /// <summary>
    /// Provider-private key vault. ApiKeyComboBox uses ApiKey (the SelectedId)
    /// for display + manage-flow; every key in this list joins the rotating
    /// KeyPool. Migrated from the legacy global-vault + ExtraApiKeyIds model
    /// in v0.9.251.
    /// </summary>
    [SettingsItemIgnore]
    public ObservableCollection<ApiKey> ApiKeys { get; init; } = [];

    /// <summary>
    /// Legacy v0.9.247–250 field. Read on deserialize so we can lift its
    /// values into <see cref="ApiKeys"/> via
    /// <see cref="WebSearchEngineSettings.MigrateLegacyVault"/>; not serialised
    /// back. Empty after migration.
    /// </summary>
    [SettingsItemIgnore]
    [JsonInclude]
    [JsonPropertyName("ExtraApiKeyIds")]
    internal List<Guid>? LegacyExtraIds { get; set; }

    [JsonIgnore]
    [DynamicLocaleKey(
        LocaleKey.WebSearchEngineProvider_ApiKey_Header,
        LocaleKey.WebSearchEngineProvider_ApiKey_Description)]
    [SettingsItem(Group = "_")]
    public SettingsControl<ApiKeyComboBox> ApiKeyControl => new(
        new ApiKeyComboBox(ApiKeys)
        {
            [!ApiKeyComboBox.SelectedIdProperty] = CompiledBinding.Create(
                (GoogleWebSearchEngineProvider x) => x.ApiKey,
                source: this,
                mode: BindingMode.TwoWay)
        });

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.WebSearchEngineProvider_SearchEngineId_Header,
        LocaleKey.WebSearchEngineProvider_SearchEngineId_Description)]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(GoogleWebSearchEngineProvider), nameof(ValidateSearchEngineId))]
    [SettingsItem(Group = "_")]
    public partial string? SearchEngineId { get; set; }

    public static ValidationResult? ValidateSearchEngineId(string? searchEngineId)
    {
        if (string.IsNullOrWhiteSpace(searchEngineId))
        {
            return new ValidationResult(LocaleResolver.ValidationErrorMessage_Required);
        }

        return ValidationResult.Success;
    }
}

[GeneratedSettingsItems]
public sealed partial class ApiKeyWebSearchEngineProvider(
    WebSearchEngineProviderId id,
    IDynamicLocaleKey headerKey,
    string iconUrl,
    string? docsUrl
) : ThirdPartyWebSearchEngineProvider
{
    [JsonIgnore]
    [SettingsItemIgnore]
    public override WebSearchEngineProviderId Id { get; } = id;

    [JsonIgnore]
    [SettingsItemIgnore]
    public override IDynamicLocaleKey HeaderKey { get; } = headerKey;

    [JsonIgnore]
    [SettingsItemIgnore]
    public override string IconUrl { get; } = iconUrl;

    [JsonIgnore]
    [SettingsItemIgnore]
    public override string? DocsUrl { get; } = docsUrl;

    [DynamicLocaleKey(
        LocaleKey.WebSearchEngineProvider_EndPoint_Header,
        LocaleKey.WebSearchEngineProvider_EndPoint_Description)]
    [SettingsItem(Group = "_")]
    public required Customizable<string> EndPoint { get; init; }

    [ObservableProperty]
    [SettingsItemIgnore]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(ApiKey), nameof(Configuration.ApiKey.Validate))]
    public partial Guid ApiKey { get; set; }

    /// <summary>See GoogleWebSearchEngineProvider.ApiKeys for semantics.</summary>
    [SettingsItemIgnore]
    public ObservableCollection<ApiKey> ApiKeys { get; init; } = [];

    /// <summary>See GoogleWebSearchEngineProvider.LegacyExtraIds.</summary>
    [SettingsItemIgnore]
    [JsonInclude]
    [JsonPropertyName("ExtraApiKeyIds")]
    internal List<Guid>? LegacyExtraIds { get; set; }

    [JsonIgnore]
    [DynamicLocaleKey(
        LocaleKey.WebSearchEngineProvider_ApiKey_Header,
        LocaleKey.WebSearchEngineProvider_ApiKey_Description)]
    [SettingsItem(Group = "_")]
    public SettingsControl<ApiKeyComboBox> ApiKeyControl => new(
        new ApiKeyComboBox(ApiKeys)
        {
            [!ApiKeyComboBox.SelectedIdProperty] = CompiledBinding.Create(
                (ApiKeyWebSearchEngineProvider x) => x.ApiKey,
                source: this,
                mode: BindingMode.TwoWay)
        });
}

[GeneratedSettingsItems]
public sealed partial class OptionalApiKeyWebSearchEngineProvider(
    WebSearchEngineProviderId id,
    IDynamicLocaleKey headerKey,
    string iconUrl,
    string? docsUrl
) : ThirdPartyWebSearchEngineProvider
{
    [JsonIgnore]
    [SettingsItemIgnore]
    public override WebSearchEngineProviderId Id { get; } = id;

    [JsonIgnore]
    [SettingsItemIgnore]
    public override IDynamicLocaleKey HeaderKey { get; } = headerKey;

    [JsonIgnore]
    [SettingsItemIgnore]
    public override string IconUrl { get; } = iconUrl;

    [JsonIgnore]
    [SettingsItemIgnore]
    public override string? DocsUrl { get; } = docsUrl;

    [DynamicLocaleKey(
        LocaleKey.WebSearchEngineProvider_EndPoint_Header,
        LocaleKey.WebSearchEngineProvider_EndPoint_Description)]
    [SettingsItem(Group = "_")]
    public required Customizable<string> EndPoint { get; init; }

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial Guid ApiKey { get; set; }

    /// <summary>See GoogleWebSearchEngineProvider.ApiKeys for semantics.</summary>
    [SettingsItemIgnore]
    public ObservableCollection<ApiKey> ApiKeys { get; init; } = [];

    /// <summary>See GoogleWebSearchEngineProvider.LegacyExtraIds.</summary>
    [SettingsItemIgnore]
    [JsonInclude]
    [JsonPropertyName("ExtraApiKeyIds")]
    internal List<Guid>? LegacyExtraIds { get; set; }

    [JsonIgnore]
    [DynamicLocaleKey(
        LocaleKey.WebSearchEngineProvider_ApiKey_Header_Optional,
        LocaleKey.WebSearchEngineProvider_ApiKey_Description)]
    [SettingsItem(Group = "_")]
    public SettingsControl<ApiKeyComboBox> ApiKeyControl => new(
        new ApiKeyComboBox(ApiKeys)
        {
            [!ApiKeyComboBox.SelectedIdProperty] = CompiledBinding.Create(
                (OptionalApiKeyWebSearchEngineProvider x) => x.ApiKey,
                source: this,
                mode: BindingMode.TwoWay)
        });
}

[GeneratedSettingsItems]
public sealed partial class SearXNGWebSearchEngineProvider : ThirdPartyWebSearchEngineProvider
{
    [JsonIgnore]
    [SettingsItemIgnore]
    public override WebSearchEngineProviderId Id => WebSearchEngineProviderId.SearXNG;

    [JsonIgnore]
    [SettingsItemIgnore]
    public override IDynamicLocaleKey HeaderKey { get; } = new DirectLocaleKey("SearXNG");

    [JsonIgnore]
    [SettingsItemIgnore]
    public override string IconUrl => "avares://Everywhere.Core/Assets/Icons/searxng-color.svg";

    [JsonIgnore]
    [SettingsItemIgnore]
    public override string DocsUrl => "https://docs.searxng.org";

    [DynamicLocaleKey(
        LocaleKey.WebSearchEngineProvider_EndPoint_Header,
        LocaleKey.WebSearchEngineProvider_EndPoint_Description)]
    public Customizable<string> EndPoint { get; } = new("https://searxng.example.com/search", isDefaultValueReadonly: true);
}

[GeneratedSettingsItems]
public sealed partial class WebSearchEngineSettings : ObservableObject, System.Text.Json.Serialization.IJsonOnDeserialized
{
    [SettingsItemIgnore]
    public ObservableImmutableDictionary<WebSearchEngineProviderId, IWebSearchEngineProvider> Providers { get; }

    [SettingsItemIgnore]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedProvider))]
    public partial WebSearchEngineProviderId SelectedProviderId { get; set; }

    [JsonIgnore]
    [SettingsItemIgnore]
    public IWebSearchEngineProvider? SelectedProvider
    {
        get => Providers.GetValueOrDefault(SelectedProviderId);
        set
        {
            if (Equals(SelectedProviderId, value?.Id)) return;
            SelectedProviderId = value?.Id ?? default;
        }
    }

    /// <summary>
    /// Legacy global key vault. Pre-v0.9.251 every provider referenced
    /// keys here by Guid. We migrate those to per-provider <c>ApiKeys</c>
    /// in <see cref="MigrateLegacyVault"/> right after deserialization,
    /// then leave the list as-is so manual edits to settings.json don't
    /// surprise users.
    /// </summary>
    [ObservableProperty]
    [SettingsItemIgnore]
    public partial ObservableCollection<ApiKey> ApiKeys { get; set; }

    /// <summary>
    /// Walk every provider and lift legacy single-key + extra-key Guid
    /// references into the provider's private <c>ApiKeys</c> list. No-op
    /// when ApiKeys is already populated (fresh install or already
    /// migrated). Safe to call multiple times.
    /// </summary>
    void System.Text.Json.Serialization.IJsonOnDeserialized.OnDeserialized() => MigrateLegacyVault();

    public void MigrateLegacyVault()
    {
        if (ApiKeys.Count == 0) return;
        // Tolerate duplicate ids in user-edited settings.json: prefer the
        // first occurrence and ignore the rest, instead of throwing inside
        // OnDeserialized (which would brick startup).
        var byId = new Dictionary<Guid, ApiKey>();
        foreach (var k in ApiKeys)
        {
            if (k.Id == Guid.Empty) continue;
            byId.TryAdd(k.Id, k);
        }
        foreach (var entry in Providers.Values)
        {
            ObservableCollection<ApiKey>? targetList;
            Guid primary;
            List<Guid>? legacyExtras;
            switch (entry)
            {
                case GoogleWebSearchEngineProvider g:
                    targetList = g.ApiKeys; primary = g.ApiKey;
                    legacyExtras = g.LegacyExtraIds;
                    g.LegacyExtraIds = null;
                    break;
                case ApiKeyWebSearchEngineProvider a:
                    targetList = a.ApiKeys; primary = a.ApiKey;
                    legacyExtras = a.LegacyExtraIds;
                    a.LegacyExtraIds = null;
                    break;
                case OptionalApiKeyWebSearchEngineProvider o:
                    targetList = o.ApiKeys; primary = o.ApiKey;
                    legacyExtras = o.LegacyExtraIds;
                    o.LegacyExtraIds = null;
                    break;
                default:
                    continue;
            }
            if (targetList.Count > 0) continue;

            if (primary != Guid.Empty && byId.TryGetValue(primary, out var pk))
                targetList.Add(pk);

            if (legacyExtras is not null)
            {
                foreach (var id in legacyExtras)
                {
                    if (id == Guid.Empty || id == primary) continue;
                    if (byId.TryGetValue(id, out var k) && !targetList.Contains(k))
                        targetList.Add(k);
                }
            }
        }
    }

    public WebSearchEngineSettings()
    {
        ApiKeys = [];
        Providers = new ObservableImmutableDictionary<WebSearchEngineProviderId, IWebSearchEngineProvider>(
        [
            new KeyValuePair<WebSearchEngineProviderId, IWebSearchEngineProvider>(
                WebSearchEngineProviderId.Official,
                new OfficialWebSearchEngineProvider()),
            new KeyValuePair<WebSearchEngineProviderId, IWebSearchEngineProvider>(
                WebSearchEngineProviderId.AnySearch,
                new OptionalApiKeyWebSearchEngineProvider(
                    WebSearchEngineProviderId.AnySearch,
                    new DirectLocaleKey("AnySearch"),
                    "avares://Everywhere.Core/Assets/Icons/anysearch-color.png",
                    "https://www.anysearch.com")
                {
                    EndPoint = new Customizable<string>("https://api.anysearch.com/v1/search", isDefaultValueReadonly: true)
                }),
            new KeyValuePair<WebSearchEngineProviderId, IWebSearchEngineProvider>(
                WebSearchEngineProviderId.Bocha,
                new ApiKeyWebSearchEngineProvider(
                    WebSearchEngineProviderId.Bocha,
                    new DynamicLocaleKey(LocaleKey.WebSearchEngineProvider_Bocha),
                    "avares://Everywhere.Core/Assets/Icons/bocha-color.png",
                    "https://open.bochaai.com")
                {
                    EndPoint = new Customizable<string>("https://api.bocha.cn/v1/web-search", isDefaultValueReadonly: true)
                }),
            new KeyValuePair<WebSearchEngineProviderId, IWebSearchEngineProvider>(
                WebSearchEngineProviderId.Brave,
                new ApiKeyWebSearchEngineProvider(
                    WebSearchEngineProviderId.Brave,
                    new DirectLocaleKey("Brave"),
                    "avares://Everywhere.Core/Assets/Icons/brave-color.png",
                    "https://brave.com/search/api")
                {
                    EndPoint = new Customizable<string>("https://api.search.brave.com/res/v1/web/search", isDefaultValueReadonly: true)
                }),
            new KeyValuePair<WebSearchEngineProviderId, IWebSearchEngineProvider>(
                WebSearchEngineProviderId.Google,
                new GoogleWebSearchEngineProvider()),
            new KeyValuePair<WebSearchEngineProviderId, IWebSearchEngineProvider>(
                WebSearchEngineProviderId.Jina,
                new ApiKeyWebSearchEngineProvider(
                    WebSearchEngineProviderId.Jina,
                    new DirectLocaleKey("Jina"),
                    "avares://Everywhere.Core/Assets/Icons/jina-light.svg",
                    "https://jina.ai")
                {
                    EndPoint = new Customizable<string>("https://s.jina.ai", isDefaultValueReadonly: true)
                }),
            new KeyValuePair<WebSearchEngineProviderId, IWebSearchEngineProvider>(
                WebSearchEngineProviderId.SearXNG,
                new SearXNGWebSearchEngineProvider()),
            new KeyValuePair<WebSearchEngineProviderId, IWebSearchEngineProvider>(
                WebSearchEngineProviderId.Tavily,
                new ApiKeyWebSearchEngineProvider(
                    WebSearchEngineProviderId.Tavily,
                    new DirectLocaleKey("Tavily"),
                    "avares://Everywhere.Core/Assets/Icons/tavily-color.svg",
                    "https://tavily.com")
                {
                    EndPoint = new Customizable<string>("https://api.tavily.com/search", isDefaultValueReadonly: true)
                }),
            new KeyValuePair<WebSearchEngineProviderId, IWebSearchEngineProvider>(
                WebSearchEngineProviderId.UniFuncs,
                new ApiKeyWebSearchEngineProvider(
                    WebSearchEngineProviderId.UniFuncs,
                    new DirectLocaleKey("UniFuncs"),
                    "avares://Everywhere.Core/Assets/Icons/unifuncs-color.png",
                    "https://www.unifuncs.com")
                {
                    EndPoint = new Customizable<string>("https://api.unifuncs.com/api/web-search/search", isDefaultValueReadonly: true)
                }),
            new KeyValuePair<WebSearchEngineProviderId, IWebSearchEngineProvider>(
                WebSearchEngineProviderId.TinyFish,
                new ApiKeyWebSearchEngineProvider(
                    WebSearchEngineProviderId.TinyFish,
                    new DirectLocaleKey("TinyFish"),
                    "avares://Everywhere.Core/Assets/Icons/tinyfish-color.svg",
                    "https://tinyfish.ai")
                {
                    EndPoint = new Customizable<string>("https://api.search.tinyfish.ai", isDefaultValueReadonly: true)
                }),
        ]);
    }
}