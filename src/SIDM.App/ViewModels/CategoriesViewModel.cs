using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using SIDM.App.Services;
using SIDM.Core.Models;
using SIDM.Core.Persistence;
using SIDM.Core.Scheduling;

namespace SIDM.App.ViewModels;

/// <summary>
/// One entry in the categories sidebar. Wraps either a user-defined
/// <see cref="Category"/> row or one of the synthetic groups (All Downloads,
/// Unfinished, Finished). Owns its own filter predicate that the rows
/// <see cref="ICollectionView"/> evaluates.
/// </summary>
public sealed partial class CategoryNodeViewModel : ObservableObject
{
    public CategoryNodeViewModel(string name, string? glyph, string? color, Predicate<DownloadRowViewModel> filter)
    {
        Name = name;
        Glyph = glyph;
        Color = color;
        Filter = filter;
    }

    public string Name { get; }
    public string? Glyph { get; }
    public string? Color { get; }
    public Predicate<DownloadRowViewModel> Filter { get; }
}

/// <summary>
/// Backs the left-hand categories sidebar. Loads user categories from the
/// repository, prepends the synthetic "All Downloads" entry and appends
/// "Unfinished" / "Finished" — mirroring IDM's layout. Exposes a
/// <see cref="RowsView"/> that the grid binds to; selecting a node updates
/// the view's filter and the grid re-projects automatically.
/// </summary>
public partial class CategoriesViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DownloadsViewModel _downloads;
    private List<Category> _categoriesSnapshot = new();

    public CategoriesViewModel(IServiceScopeFactory scopeFactory, DownloadsViewModel downloads)
    {
        _scopeFactory = scopeFactory;
        _downloads = downloads;

        // CollectionViewSource gives us a filterable, sort-stable wrapper over
        // the rows collection. The grid binds to RowsView, not Rows directly,
        // so changing the selected node re-filters in place.
        RowsView = CollectionViewSource.GetDefaultView(_downloads.Rows);
        RowsView.Filter = obj => obj is DownloadRowViewModel row && (_selectedNode?.Filter(row) ?? true);
    }

    public ObservableCollection<CategoryNodeViewModel> Nodes { get; } = new();
    public ICollectionView RowsView { get; }

    [ObservableProperty]
    private CategoryNodeViewModel? _selectedNode;

    partial void OnSelectedNodeChanged(CategoryNodeViewModel? value)
    {
        RowsView.Refresh();
    }

    /// <summary>Loads categories from the repository and rebuilds the sidebar.</summary>
    public async Task LoadAsync()
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICategoryRepository>();
        _categoriesSnapshot = (await repo.GetAllAsync()).ToList();

        Nodes.Clear();

        // "All Downloads" — accepts every row.
        var all = new CategoryNodeViewModel("All Downloads", glyph: "", color: null, filter: _ => true);
        Nodes.Add(all);

        // User-defined categories — match by extension using the same matcher
        // the engine uses when categorizing a freshly-captured download.
        foreach (var c in _categoriesSnapshot.OrderBy(c => c.Name))
        {
            var capturedId = c.Id;
            var capturedExts = c.Extensions ?? string.Empty;
            Nodes.Add(new CategoryNodeViewModel(
                name: c.Name,
                glyph: null,
                color: c.Color,
                // A row belongs to a category if its CategoryId matches OR its
                // current extension is in the category's extension list. The
                // OR handles legacy rows captured before categories existed
                // and rows whose category was renamed.
                filter: row => row.CategoryId == capturedId || ExtensionInList(row.ExtensionLower, capturedExts)));
        }

        // Synthetic state buckets — match IDM's bottom-of-sidebar entries.
        Nodes.Add(new CategoryNodeViewModel("Unfinished", glyph: null, color: null,
            filter: r => r.Status is DownloadStatus.Queued
                or DownloadStatus.Probing
                or DownloadStatus.Downloading
                or DownloadStatus.Paused
                or DownloadStatus.Failed));
        Nodes.Add(new CategoryNodeViewModel("Finished", glyph: null, color: null,
            filter: r => r.Status == DownloadStatus.Completed));

        SelectedNode = all;
    }

    private static bool ExtensionInList(string ext, string commaList)
    {
        if (string.IsNullOrEmpty(ext) || string.IsNullOrEmpty(commaList)) return false;
        foreach (var raw in commaList.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(raw.TrimStart('.'), ext, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
