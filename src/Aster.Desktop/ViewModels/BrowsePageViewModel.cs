using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core.Browsing;
using Aster.Core.Jellyfin;
using CommunityToolkit.Mvvm.ComponentModel;
using Query = System.Collections.Generic.Dictionary<string, string?>;

namespace Aster.Desktop.ViewModels;

// A browse page is one long list of rows, so that a library of thousands
// scrolls smoothly: only the rows on screen are made. These are its kinds.

public abstract record BrowseRow;

public sealed record HeroRow(HeroModel Hero, BrowsePageViewModel Page) : BrowseRow;

/// <summary>The page's title, with its sort picker when it has a grid.</summary>
public sealed record TitleRow(string Title, BrowsePageViewModel Page) : BrowseRow;

public sealed record LandscapeShelfRow(Shelf Shelf) : BrowseRow;

public sealed record PosterShelfRow(Shelf Shelf) : BrowseRow;

/// <summary>Libraries, as wide tiles with their names over them.</summary>
public sealed record TileShelfRow(Shelf Shelf) : BrowseRow;

/// <summary>"All Movies", or a letter over its section.</summary>
public sealed record HeadingRow(string Title, bool IsLetter = false) : BrowseRow;

public sealed record PosterCardsRow(IReadOnlyList<ItemCard> Cards) : BrowseRow;

public sealed record LandscapeCardsRow(IReadOnlyList<ItemCard> Cards) : BrowseRow;

/// <summary>Loading, nothing here, or why it couldn't load.</summary>
public sealed record StatusRow(string Title, string? Detail = null, bool IsLoading = false) : BrowseRow;

/// <summary>A person's photo, name and biography.</summary>
public sealed record PersonRow(string Name, Uri? Photo, string? Overview) : BrowseRow;

/// <summary>
/// Pages a query's results in from the server, 100 at a time. Items whose
/// ids are in the exclusions are skipped (anime, which has its own tab). A
/// port of the Apple app's GridModel.
/// </summary>
public sealed class GridLoader(JellyfinClient client)
{
    public List<BaseItem> Items { get; } = [];
    public string? ErrorMessage { get; private set; }
    public bool IsLoading { get; private set; }

    /// <summary>How far into the server's results loading has got, counting items left out, so pages carry on from the right place.</summary>
    private int fetched;
    private int total;

    /// <summary>Bumped on every reset, so a page still loading from before it is dropped instead of landing in the new results.</summary>
    private int generation;

    public bool HasMore => fetched < total;

    public void Reset()
    {
        Items.Clear();
        fetched = 0;
        total = 0;
        generation++;
        IsLoading = false;
        ErrorMessage = null;
    }

    /// <summary>The next page; false when nothing was loaded (already loading, or it failed).</summary>
    public async Task<bool> LoadNextAsync(Query query, IReadOnlySet<string> excluding)
    {
        if (IsLoading) return false;
        var started = generation;
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var page = new Query(query) { ["startIndex"] = fetched.ToString(), ["limit"] = "100" };
            var result = await client.ItemsAsync(page);
            if (started != generation) return false;
            fetched += result.Items.Count;
            total = result.TotalRecordCount ?? fetched;
            var known = Items.Select(i => i.Id).ToHashSet();
            Items.AddRange(result.Items.Where(i => !excluding.Contains(i.Id) && known.Add(i.Id)));
            System.Diagnostics.Debug.WriteLine($"Grid: {fetched} of {total} fetched, {Items.Count} shown, for {string.Join("&", page.Select(p => $"{p.Key}={p.Value}"))}");
            return true;
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException)
        {
            if (started == generation) ErrorMessage = error.Message;
            return false;
        }
        finally
        {
            if (started == generation) IsLoading = false;
        }
    }
}

/// <summary>
/// A page of the sidebar's tabs, or a library opened from one: rows above
/// (a banner, a title, shelves), and below them, for a page with one, a grid
/// of every title, sortable, in letter sections when sorted by name.
/// </summary>
public abstract partial class BrowsePageViewModel : ViewModelBase, IRefreshable
{
    protected readonly INavigator Navigator;
    private readonly GridLoader grid;
    private IReadOnlyList<BrowseRow> top = [];
    private IReadOnlyList<IndexSection> sections = [];
    private int gridRowsStart = -1;
    private int columns = 6;

    protected BrowsePageViewModel(INavigator navigator)
    {
        Navigator = navigator;
        grid = new GridLoader(navigator.Client);
    }

    public ObservableCollection<BrowseRow> Rows { get; } = [];

    /// <summary>Opened over another page, so it has a back arrow (which its title makes room for).</summary>
    public bool IsPushed { get; set; }

    /// <summary>The banner's height: the window's, as on Home.</summary>
    [ObservableProperty] public partial double HeroHeight { get; set; } = 700;

    /// <summary>The letters of the sections, for the index down the right; empty when not sorted by name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIndex))]
    public partial IReadOnlyList<string> IndexKeys { get; set; } = [];

    public bool HasIndex => IndexKeys.Count > 0;

    // ---- What a page says about its grid ----

    /// <summary>Whether this page has a grid of titles.</summary>
    protected virtual bool HasGrid => false;

    /// <summary>Wide cards (home videos, music videos) rather than posters.</summary>
    public virtual bool LandscapeGrid => false;

    /// <summary>"All Movies"; null for no heading over the grid.</summary>
    protected virtual string? GridHeading => null;

    protected virtual Query GridQuery() => [];

    protected virtual IReadOnlySet<string> GridExcluding => new HashSet<string>();

    // ---- Sorting ----

    public IReadOnlyList<string> SortTitles { get; } = Enum.GetValues<LibrarySort>().Select(s => s.Title).ToList();
    public bool ShowsSort => HasGrid;

    [ObservableProperty] public partial int SortIndex { get; set; }

    protected LibrarySort Sort => (LibrarySort)SortIndex;

    /// <summary>The page has loaded once, so a new sort reloads its grid.</summary>
    private bool started;

    partial void OnSortIndexChanged(int value)
    {
        if (started) _ = ReloadGridAsync();
    }

    protected Query SortQuery() => new() { ["sortBy"] = Sort.Query.SortBy, ["sortOrder"] = Sort.Query.Order };

    // ---- Building the page ----

    /// <summary>Loads everything above the grid; returns its rows.</summary>
    protected abstract Task<IReadOnlyList<BrowseRow>> LoadTopAsync();

    /// <summary>Loads the page, as when first shown.</summary>
    protected async Task LoadAsync()
    {
        started = true;
        if (Rows.Count == 0) Rows.Add(new StatusRow("Loading…", IsLoading: true));
        try
        {
            top = await LoadTopAsync();
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException)
        {
            if (error is JellyfinException { StatusCode: 401 }) await Navigator.SessionExpiredAsync();
            top = [new StatusRow("Couldn't load", error.Message)];
        }
        RebuildTop();
        if (HasGrid) await ReloadGridAsync();
    }

    public virtual void Refresh() => _ = RefreshTopAsync();

    /// <summary>Freshens the rows above the grid (what's in progress); the grid stays where it was scrolled.</summary>
    private async Task RefreshTopAsync()
    {
        try { top = await LoadTopAsync(); }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException) { return; }
        RebuildTop();
    }

    /// <summary>Everything when sorted by name (for the sections), else page by page as the grid is scrolled.</summary>
    private async Task ReloadGridAsync()
    {
        grid.Reset();
        sections = [];
        IndexKeys = [];
        RebuildGrid();
        if (!await grid.LoadNextAsync(GridQuery(), GridExcluding))
        {
            RebuildGrid();
            return;
        }
        if (Sort == LibrarySort.Name)
        {
            RebuildGrid();
            while (grid.HasMore && await grid.LoadNextAsync(GridQuery(), GridExcluding)) { }
            sections = grid.HasMore ? [] : LibraryIndex.Sections(grid.Items);
            IndexKeys = sections.Count > 1 ? sections.Select(s => s.Key).ToList() : [];
        }
        RebuildGrid();
    }

    /// <summary>The grid is near the bottom of the window: the next page, unless sorted by name (loaded whole).</summary>
    public async Task LoadMoreAsync()
    {
        if (!HasGrid || Sort == LibrarySort.Name || !grid.HasMore || grid.IsLoading) return;
        if (await grid.LoadNextAsync(GridQuery(), GridExcluding)) RebuildGrid();
    }

    /// <summary>How many cards fit across, from the window's width; the grid is laid out again when it changes.</summary>
    public void SetColumns(int count)
    {
        count = Math.Max(1, count);
        if (count == columns) return;
        columns = count;
        RebuildGrid();
    }

    /// <summary>The row a letter's section starts at, to scroll to.</summary>
    public int RowOfSection(string key)
    {
        for (var i = Math.Max(gridRowsStart, 0); i < Rows.Count; i++)
        {
            if (Rows[i] is HeadingRow { IsLetter: true } heading && heading.Title == key) return i;
        }
        return -1;
    }

    private void RebuildTop()
    {
        var gridRows = gridRowsStart >= 0 ? Rows.Skip(gridRowsStart).ToList() : [];
        Rows.Clear();
        foreach (var row in top) Rows.Add(row);
        gridRowsStart = Rows.Count;
        foreach (var row in gridRows) Rows.Add(row);
    }

    /// <summary>
    /// Lays the grid out again below the rows above it. Rows that haven't
    /// changed are left alone, so new pages arriving don't disturb the ones
    /// already on screen.
    /// </summary>
    private void RebuildGrid()
    {
        if (!HasGrid) return;
        if (gridRowsStart < 0) gridRowsStart = Rows.Count;
        var rows = new List<BrowseRow>();
        if (GridHeading is { } heading && (grid.Items.Count > 0 || grid.IsLoading)) rows.Add(new HeadingRow(heading));
        if (grid.Items.Count == 0)
        {
            rows.Add(grid.ErrorMessage is { } error ? new StatusRow("Couldn't load", error)
                : grid.IsLoading ? new StatusRow("Loading…", IsLoading: true)
                : new StatusRow("Nothing here yet"));
        }
        else if (sections.Count > 1)
        {
            foreach (var section in sections)
            {
                rows.Add(new HeadingRow(section.Key, IsLetter: true));
                rows.AddRange(CardRows(section.Items));
            }
        }
        else
        {
            rows.AddRange(CardRows(grid.Items));
            if (grid.HasMore && Sort != LibrarySort.Name) rows.Add(new StatusRow("Loading…", IsLoading: true));
        }

        // Keep what's the same; replace from the first difference.
        var at = 0;
        while (at < rows.Count && gridRowsStart + at < Rows.Count && SameRow(Rows[gridRowsStart + at], rows[at])) at++;
        while (Rows.Count > gridRowsStart + at) Rows.RemoveAt(Rows.Count - 1);
        foreach (var row in rows.Skip(at)) Rows.Add(row);
    }

    private static bool SameRow(BrowseRow a, BrowseRow b) => (a, b) switch
    {
        (PosterCardsRow x, PosterCardsRow y) => x.Cards.Select(c => c.Item.Id).SequenceEqual(y.Cards.Select(c => c.Item.Id)),
        (LandscapeCardsRow x, LandscapeCardsRow y) => x.Cards.Select(c => c.Item.Id).SequenceEqual(y.Cards.Select(c => c.Item.Id)),
        _ => a == b,
    };

    private IEnumerable<BrowseRow> CardRows(IEnumerable<BaseItem> items) =>
        items.Chunk(columns).Select(chunk => LandscapeGrid
            ? (BrowseRow)new LandscapeCardsRow(chunk.Select(i => ItemCard.Landscape(Navigator, i)).ToList())
            : new PosterCardsRow(chunk.Select(i => ItemCard.Poster(Navigator, i)).ToList()));

    protected LandscapeShelfRow? LandscapeShelf(string title, IEnumerable<BaseItem> items, bool showSeriesTitle = true)
    {
        var cards = items.Select(i => ItemCard.Landscape(Navigator, i, showSeriesTitle)).ToList();
        return cards.Count > 0 ? new LandscapeShelfRow(new Shelf(title, cards)) : null;
    }

    protected PosterShelfRow? PosterShelf(string title, IEnumerable<BaseItem> items)
    {
        var cards = items.Select(i => ItemCard.Poster(Navigator, i)).ToList();
        return cards.Count > 0 ? new PosterShelfRow(new Shelf(title, cards)) : null;
    }

    protected TileShelfRow? TileShelf(string title, IEnumerable<BaseItem> libraries)
    {
        var cards = libraries.Select(l => ItemCard.Tile(Navigator, l)).ToList();
        return cards.Count > 0 ? new TileShelfRow(new Shelf(title, cards)) : null;
    }
}
