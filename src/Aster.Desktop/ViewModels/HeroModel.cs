using System;
using System.Collections.Generic;
using System.Linq;
using Aster.Core.Jellyfin;
using Aster.Desktop.Images;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>One page of a featured banner.</summary>
public sealed partial class HeroPage(INavigator navigator, BaseItem item) : ObservableObject
{
    public BaseItem Item { get; } = item;
    public Uri? Backdrop { get; } = navigator.Client.BackdropUrl(item, width: 1920);
    public Uri? Logo { get; } = navigator.Client.LogoUrl(item);
    public string Title => Item.Kind == ItemKind.Episode ? Item.SeriesName ?? Item.DisplayTitle : Item.DisplayTitle;
    public string? EpisodeLine => Item.Kind == ItemKind.Episode ? string.Join(" · ", new[] { Item.EpisodeCode, Item.Name }.OfType<string>()) : null;
    public MetadataLine Metadata { get; } = MetadataLine.For(item);
    public string? Overview => Item.Overview;
    public string PlayLabel => Item.CanResume ? "Resume" : Item.Kind == ItemKind.Series ? "Watch Now" : "Play";
    public string? Remaining => Item.RemainingText is { } left ? $"·  {left}" : null;

    /// <summary>The page opens.</summary>
    [RelayCommand] private void Open() => navigator.Open(Item);

    /// <summary>
    /// Play and Resume: the title's page opens and starts playing at once,
    /// so closing the player leaves the viewer on that page.
    /// </summary>
    [RelayCommand] private void Play() => navigator.Open(Item, playOnOpen: true);
}

/// <summary>
/// The featured banner at the top of Home and the Movies, TV Shows and Anime
/// tabs: up to 8 titles, paged with arrows, dots or the arrow keys.
/// </summary>
public sealed partial class HeroModel(INavigator navigator) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPages), nameof(Dots), nameof(HasNext))]
    public partial IReadOnlyList<HeroPage> Pages { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Dots), nameof(HasPrevious), nameof(HasNext))]
    public partial int Index { get; set; }

    public bool HasPages => Pages.Count > 0;
    public bool HasPrevious => Index > 0;
    public bool HasNext => Index < Pages.Count - 1;

    /// <summary>The page dots: true for the page showing.</summary>
    public IReadOnlyList<bool> Dots => Pages.Count > 1 ? Pages.Select((_, i) => i == Index).ToList() : [];

    [RelayCommand] private void Next() => Index = Math.Min(Index + 1, Math.Max(Pages.Count - 1, 0));
    [RelayCommand] private void Previous() => Index = Math.Max(Index - 1, 0);

    /// <summary>
    /// Shows <paramref name="items"/>, the first 8 not already picked, staying
    /// on the page the viewer was on if that title is still featured. The
    /// pictures are big and slow, so all of them are fetched ahead.
    /// </summary>
    public void Show(IEnumerable<BaseItem> items)
    {
        var picks = new List<BaseItem>();
        foreach (var item in items)
        {
            if (picks.All(p => p.Id != item.Id)) picks.Add(item);
            if (picks.Count == 8) break;
        }
        var current = Index < Pages.Count ? Pages[Index].Item.Id : null;
        var pages = picks.Select(item => new HeroPage(navigator, item)).ToList();
        Pages = pages;
        Index = Math.Max(0, pages.FindIndex(p => p.Item.Id == current));
        ImageLoader.Prefetch(pages.SelectMany(p => new[] { p.Backdrop, p.Logo }));
    }
}
