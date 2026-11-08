using System.Collections.Generic;
using System.Threading.Tasks;
using Aster.Core.Tracking;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// A title's place on the person's linked watchlist (Simkl's Plan to Watch,
/// through the server's Aster Discovery and Tracking plugin), for any film or
/// show: the library's title pages, Discover's and Seerr's. Hidden until the
/// server says it can add to a watchlist the person has linked, as on the
/// Android app.
/// </summary>
public sealed partial class WatchlistModel(INavigator navigator) : ObservableObject
{
    private WatchlistTitle? title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown), nameof(IsOn), nameof(Label))]
    public partial WatchlistState? State { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleCommand))]
    public partial bool IsWorking { get; private set; }

    /// <summary>What went wrong the last time, in words to show; cleared by the next try.</summary>
    [ObservableProperty] public partial string? Problem { get; set; }

    public bool IsShown => State is not null;
    public bool IsOn => State?.On == true;
    public string Label => IsOn ? "On Watchlist" : "Add to Watchlist";

    /// <summary>Asks where the title stands; one with no ids the sites know gets no button.</summary>
    public async Task LoadAsync(string mediaType, bool anime, IReadOnlyDictionary<string, string> ids, string? name, int? year)
    {
        if (ids.Count == 0 || navigator.Jellyfin is not { } jellyfin) return;
        title = new WatchlistTitle { MediaType = mediaType, Anime = anime, Ids = new(ids), Title = name, Year = year };
        var states = await AsterTracking.WatchlistStateAsync(jellyfin, await navigator.Tracking, mediaType, ids);
        foreach (var state in states)
        {
            if (!state.Linked) continue;
            State = state;
            return;
        }
    }

    private bool CanToggle() => !IsWorking;

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task ToggleAsync()
    {
        if (State is not { } current || title is null || navigator.Jellyfin is not { } jellyfin) return;
        IsWorking = true;
        try
        {
            var on = await AsterTracking.SetWatchlistAsync(jellyfin, current.Service, current.On != true, title);
            State = current with { On = on };
            Problem = null;
        }
        catch (WatchlistException error)
        {
            Problem = error.Message;
        }
        finally
        {
            IsWorking = false;
        }
    }
}
