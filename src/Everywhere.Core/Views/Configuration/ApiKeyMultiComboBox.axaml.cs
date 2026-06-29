using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls.Primitives;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;

namespace Everywhere.Views;

/// <summary>
/// Multi-select API-key picker. Companion to <see cref="ApiKeyComboBox"/> for
/// per-provider "extra" key pools — agent rotates through these on rate-limit.
///
/// Shape:
///  - Items come from the shared <c>ObservableCollection&lt;ApiKey&gt;</c>
///    vault the user manages via ApiKeyComboBox / ManageApiKeyForm.
///  - <see cref="SelectedIds"/> is the bound output; provider settings
///    expose an <c>ObservableCollection&lt;Guid&gt;</c> that we write into.
///  - <see cref="ExcludedIds"/> hides the provider's primary key so the
///    same key can't be selected twice. (Bind to the same source as
///    <c>ApiKeyComboBox.SelectedIdProperty</c>.)
/// </summary>
public sealed partial class ApiKeyMultiComboBox : TemplatedControl
{
    public static readonly StyledProperty<ObservableCollection<Guid>?> SelectedIdsProperty =
        AvaloniaProperty.Register<ApiKeyMultiComboBox, ObservableCollection<Guid>?>(nameof(SelectedIds));

    public ObservableCollection<Guid>? SelectedIds
    {
        get => GetValue(SelectedIdsProperty);
        set => SetValue(SelectedIdsProperty, value);
    }

    public static readonly StyledProperty<Guid> ExcludedIdProperty =
        AvaloniaProperty.Register<ApiKeyMultiComboBox, Guid>(nameof(ExcludedId));

    public Guid ExcludedId
    {
        get => GetValue(ExcludedIdProperty);
        set => SetValue(ExcludedIdProperty, value);
    }

    public static readonly DirectProperty<ApiKeyMultiComboBox, string> SummaryProperty =
        AvaloniaProperty.RegisterDirect<ApiKeyMultiComboBox, string>(
            nameof(Summary),
            o => o.Summary);

    private string _summary = "No extra keys";
    public string Summary
    {
        get => _summary;
        private set => SetAndRaise(SummaryProperty, ref _summary, value);
    }

    public ObservableCollection<Row> AvailableKeys { get; } = [];

    private readonly ObservableCollection<ApiKey> _source;
    private bool _suppressRebuild;

    public ApiKeyMultiComboBox(ObservableCollection<ApiKey> source)
    {
        _source = source;
        Rebuild();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _source.CollectionChanged += OnSourceChanged;
        // Re-subscribe to SelectedIds — same instance survives the
        // detach/re-attach cycle and OnPropertyChanged won't fire because
        // the property reference is unchanged.
        if (SelectedIds is { } col) col.CollectionChanged += OnSelectedChanged;
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _source.CollectionChanged -= OnSourceChanged;
        DetachSelectedIds();
        DetachRowHandlers();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedIdsProperty)
        {
            if (change.OldValue is ObservableCollection<Guid> oldCol)
                oldCol.CollectionChanged -= OnSelectedChanged;
            if (change.NewValue is ObservableCollection<Guid> newCol)
                newCol.CollectionChanged += OnSelectedChanged;
            Rebuild();
        }
        else if (change.Property == ExcludedIdProperty)
        {
            // Primary key just changed — if it was in our extra set, drop
            // it so BuildPool doesn't see the same key twice.
            if (change.NewValue is Guid newExcluded && newExcluded != Guid.Empty)
                SelectedIds?.Remove(newExcluded);
            Rebuild();
        }
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnSelectedChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Self-induced changes (a user clicking a checkbox) only need a
        // summary refresh — the Row state is already correct. Skip the
        // full Rebuild which would re-template every flyout entry and
        // flicker / close the popup.
        if (_suppressRebuild)
        {
            UpdateSummary();
            return;
        }
        Rebuild();
    }

    private void DetachSelectedIds()
    {
        if (SelectedIds is { } col) col.CollectionChanged -= OnSelectedChanged;
    }

    private void DetachRowHandlers()
    {
        foreach (var r in AvailableKeys)
            r.PropertyChanged -= OnRowPropertyChanged;
    }

    private void Rebuild()
    {
        DetachRowHandlers();
        AvailableKeys.Clear();

        var selected = SelectedIds ?? [];
        var excluded = ExcludedId;
        foreach (var k in _source)
        {
            if (k.Id == Guid.Empty) continue;
            if (k.Id == excluded) continue;
            var row = new Row
            {
                Id = k.Id,
                Name = k.Name ?? string.Empty,
                IsChecked = selected.Contains(k.Id),
            };
            row.PropertyChanged += OnRowPropertyChanged;
            AvailableKeys.Add(row);
        }

        UpdateSummary();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not Row row || e.PropertyName != nameof(Row.IsChecked)) return;
        var col = SelectedIds;
        if (col is null) return;

        // Mutation about to fire OnSelectedChanged — short-circuit it so
        // the flyout doesn't re-template on every click.
        _suppressRebuild = true;
        try
        {
            if (row.IsChecked)
            {
                if (!col.Contains(row.Id)) col.Add(row.Id);
            }
            else
            {
                col.Remove(row.Id);
            }
        }
        finally
        {
            _suppressRebuild = false;
        }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var n = SelectedIds?.Count ?? 0;
        Summary = n switch
        {
            0 => "No extra keys",
            1 => "1 extra key",
            _ => $"{n} extra keys",
        };
    }

    public sealed partial class Row : ObservableObject
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;

        [ObservableProperty]
        public partial bool IsChecked { get; set; }
    }
}
