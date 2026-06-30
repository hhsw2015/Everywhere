using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Everywhere.Configuration;
using ShadUI;

namespace Everywhere.Views;

/// <summary>
/// List editor for a provider's private <c>ApiKeys</c> collection. Each row
/// shows one key with a remove button; the footer "Add API Key" button
/// pops the same <see cref="CreateApiKeyForm"/> the legacy
/// <see cref="ApiKeyComboBox"/> uses, then appends the new key to
/// <see cref="ItemsSource"/>. Designed for web-search providers where the
/// whole list joins the rotating <c>KeyPool</c> at runtime — there's no
/// "primary" concept, so no single-select needed.
/// </summary>
public sealed partial class ApiKeyListEditor : TemplatedControl
{
    public static readonly StyledProperty<ObservableCollection<ApiKey>?> ItemsSourceProperty =
        AvaloniaProperty.Register<ApiKeyListEditor, ObservableCollection<ApiKey>?>(nameof(ItemsSource));

    public ObservableCollection<ApiKey>? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>
    /// Default name shown in the create dialog (e.g. "tinyfish-1"). The
    /// provider passes its id-prefixed default; users overwrite freely.
    /// </summary>
    public static readonly StyledProperty<string?> DefaultNameProperty =
        AvaloniaProperty.Register<ApiKeyListEditor, string?>(nameof(DefaultName));

    public string? DefaultName
    {
        get => GetValue(DefaultNameProperty);
        set => SetValue(DefaultNameProperty, value);
    }

    public IAsyncRelayCommand AddCommand { get; }
    public IRelayCommand<ApiKey> RemoveCommand { get; }

    public ApiKeyListEditor()
    {
        AddCommand = new AsyncRelayCommand(AddApiKeyAsync);
        RemoveCommand = new RelayCommand<ApiKey>(RemoveApiKey);
    }

    private async Task AddApiKeyAsync(CancellationToken cancellationToken)
    {
        var defaultName = string.IsNullOrEmpty(DefaultName)
            ? "API Key"
            : DefaultName!;
        var form = new CreateApiKeyForm(defaultName);
        var result = await ServiceLocator.Resolve<DialogManager>()
            .CreateDialog(form, LocaleResolver.ApiKeyComboBox_AddApiKey)
            .WithPrimaryButton(
                LocaleResolver.Common_OK,
                (_, e) => e.Cancel = !form.ApiKey.ValidateAndSave())
            .WithCancelButton(LocaleResolver.Common_Cancel)
            .ShowAsync(cancellationToken);
        if (result != DialogResult.Primary) return;

        ItemsSource?.Add(form.ApiKey);
    }

    private void RemoveApiKey(ApiKey? key)
    {
        if (key is null || ItemsSource is null) return;
        ItemsSource.Remove(key);
    }
}
