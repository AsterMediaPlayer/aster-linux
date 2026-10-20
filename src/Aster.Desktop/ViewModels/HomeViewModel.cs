using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// A first signed-in screen: the user's libraries and what they were
/// watching, enough to prove the client against a real server. The real
/// Home (banner and shelves) comes later.
/// </summary>
public partial class HomeViewModel : ViewModelBase
{
    private readonly JellyfinClient client;
    private readonly Func<JellyfinClient, bool, Task> signOut;

    public HomeViewModel(JellyfinClient client, StoredSession session, Func<JellyfinClient, bool, Task> signOut)
    {
        this.client = client;
        this.signOut = signOut;
        Heading = $"{session.UserName} on {session.ServerName ?? session.ServerUrl.Host}";
        _ = LoadAsync();
    }

    public string Heading { get; }

    [ObservableProperty]
    public partial IReadOnlyList<string> Libraries { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<string> ContinueWatching { get; set; } = [];

    [ObservableProperty]
    public partial string? Status { get; set; } = "Loading…";

    private async Task LoadAsync()
    {
        try
        {
            var views = client.UserViewsAsync();
            var resume = client.ResumeAsync(limit: 12);
            Libraries = (await views).Select(view => view.DisplayTitle).ToList();
            ContinueWatching = (await resume)
                .Select(item => item.SeriesName is { } series ? $"{series} · {item.EpisodeCode} · {item.RemainingText}" : $"{item.DisplayTitle} · {item.RemainingText}")
                .ToList();
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

    [RelayCommand]
    private Task SignOutAsync() => signOut(client, false);
}
