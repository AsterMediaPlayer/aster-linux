using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>Something on Home to click and play.</summary>
public sealed record HomeEntry(string Title, string? Detail, IRelayCommand Play);

/// <summary>
/// A first signed-in screen: the user's libraries, what they were watching
/// and what's next, enough to try the client and player against a real
/// server. The real Home (banner and shelves) comes later.
/// </summary>
public partial class HomeViewModel : ViewModelBase
{
    private readonly JellyfinClient client;
    private readonly Func<JellyfinClient, bool, Task> signOut;
    private readonly Action<BaseItem> play;

    public HomeViewModel(JellyfinClient client, StoredSession session, Func<JellyfinClient, bool, Task> signOut, Action<BaseItem> play)
    {
        this.client = client;
        this.signOut = signOut;
        this.play = play;
        Heading = $"{session.UserName} on {session.ServerName ?? session.ServerUrl.Host}";
        Refresh();
    }

    public string Heading { get; }

    [ObservableProperty]
    public partial IReadOnlyList<string> Libraries { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<HomeEntry> ContinueWatching { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<HomeEntry> NextUp { get; set; } = [];

    [ObservableProperty]
    public partial string? Status { get; set; } = "Loading…";

    /// <summary>Loads it all again, as after watching something.</summary>
    public void Refresh() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            var views = client.UserViewsAsync();
            var resume = client.ResumeAsync(limit: 12);
            var nextUp = client.NextUpAsync(limit: 12);
            Libraries = (await views).Select(view => view.DisplayTitle).ToList();
            ContinueWatching = (await resume).Select(Entry).ToList();
            NextUp = (await nextUp).Select(Entry).ToList();
            Status = null;
        }
        catch (JellyfinException error) when (error.StatusCode == 401)
        {
            await signOut(client, true);
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException)
        {
            Status = error.Message;
        }
    }

    private HomeEntry Entry(BaseItem item)
    {
        var title = item.SeriesName is { } series ? $"{series} · {item.EpisodeCode}" : item.DisplayTitle;
        var detail = item.SeriesName is null ? item.RemainingText : string.Join(" · ", new[] { item.DisplayTitle, item.RemainingText }.OfType<string>());
        return new HomeEntry(title, detail, new RelayCommand(() => play(item)));
    }

    [RelayCommand]
    private Task SignOutAsync() => signOut(client, false);
}
