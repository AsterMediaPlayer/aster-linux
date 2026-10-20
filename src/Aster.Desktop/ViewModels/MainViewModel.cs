using System;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using Aster.Core.Playback;
using Aster.Desktop.Images;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aster.Desktop.ViewModels;

/// <summary>What the window shows: sign-in, the app (sidebar and pages), or the player over it all.</summary>
public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial ViewModelBase? CurrentPage { get; set; }

    private ShellViewModel? shell;

    /// <summary>Signed in from the ASTER_* variables: signing out leaves the saved session alone.</summary>
    private bool developmentSession;

    /// <param name="playItemId">A title to play straight away (<c>--play &lt;id&gt;</c>), for development.</param>
    /// <param name="maxBitrate">A quality cap for that title (<c>--max-bitrate</c>), to try converted streams.</param>
    /// <param name="startSeconds">Where to start that title (<c>--start</c>), instead of its resume point.</param>
    /// <param name="openItemId">A title's page to open straight away (<c>--open &lt;id&gt;</c>), for development.</param>
    /// <param name="tab">A sidebar tab to start on (<c>--tab movies</c>), for development.</param>
    /// <param name="sort">That tab's sort (<c>--sort DateAdded</c>), for development.</param>
    public MainViewModel(string? playItemId = null, int? maxBitrate = null, double? startSeconds = null, string? openItemId = null, AppTab? tab = null,
        Aster.Core.Browsing.LibrarySort? sort = null)
    {
        var development = DevelopmentSession();
        developmentSession = development is not null;
        if ((development ?? StoredSession.Load()) is { } session)
        {
            var client = new JellyfinClient(session.ServerUrl, session.AccessToken, session.UserId);
            ShowShell(client, session);
            if (tab is { } startTab) shell!.ShowTab(startTab);
            if (sort is { } startSort && shell!.CurrentPage is BrowsePageViewModel page) page.SortIndex = (int)startSort;
            if (openItemId is not null) _ = OpenByIdAsync(client, openItemId);
            if (playItemId is not null) _ = PlayByIdAsync(client, playItemId, maxBitrate, startSeconds);
        }
        else
        {
            ShowSignIn();
        }
    }

    /// <summary>
    /// In a debug build, ASTER_SERVER, ASTER_TOKEN and ASTER_USER_ID sign in
    /// without touching the saved session, for trying the app against a
    /// server with an API key.
    /// </summary>
    private static StoredSession? DevelopmentSession()
    {
#if DEBUG
        var server = Environment.GetEnvironmentVariable("ASTER_SERVER");
        var token = Environment.GetEnvironmentVariable("ASTER_TOKEN");
        var user = Environment.GetEnvironmentVariable("ASTER_USER_ID");
        if (server is not null && token is not null && user is not null)
        {
            return new StoredSession { ServerUrl = new Uri(server), UserId = user, UserName = "Development", AccessToken = token };
        }
#endif
        return null;
    }

    private void ShowSignIn(string? message = null)
    {
        shell = null;
        ImageLoader.Client = null;
        CurrentPage = new SignInViewModel(ShowShell) { Message = message };
    }

    private void ShowShell(JellyfinClient client, StoredSession session)
    {
        ImageLoader.Client = client;
        shell = new ShellViewModel(client, session, (entry, closed) => Play(client, entry, closed), expired => SignOutAsync(client, expired));
        CurrentPage = shell;
    }

    /// <summary>Plays over everything; closing comes back to the page it was played from.</summary>
    private void Play(JellyfinClient client, PlaybackQueueItem entry, Action<BaseItem?>? closed)
    {
        CurrentPage = new PlayerViewModel(client, entry, last =>
        {
            CurrentPage = shell;
            closed?.Invoke(last);
        });
    }

    /// <summary>Opens a page for <c>--open</c>: looked up first, so a library or person opens as one would from a card.</summary>
    private async Task OpenByIdAsync(JellyfinClient client, string itemId)
    {
        try { shell?.Open(await client.ItemAsync(itemId)); }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException) { }
    }

    /// <summary>Plays a title from where it was left (or <paramref name="startSeconds"/>), for <c>--play</c>.</summary>
    private async Task PlayByIdAsync(JellyfinClient client, string itemId, int? maxBitrate, double? startSeconds)
    {
        try
        {
            var item = await client.ItemAsync(itemId);
            var start = startSeconds is { } seconds ? (long)(seconds * 10_000_000) : item.UserData?.PlaybackPositionTicks ?? 0;
            shell?.Play(new PlaybackQueueItem { ItemId = item.Id, StartTicks = start, MaxBitrate = maxBitrate });
        }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException) { }
    }

    private async Task SignOutAsync(JellyfinClient client, bool expired)
    {
        if (developmentSession)
        {
            // Neither ends the key's access nor forgets the real sign-in.
            developmentSession = false;
            ShowSignIn("Signed out of the development session.");
            return;
        }
        if (!expired) await client.LogoutAsync();
        StoredSession.Clear();
        ShowSignIn(expired ? "Your session has ended. Please sign in again." : null);
    }

    /// <summary>The app is closing: a title playing reports where it stopped.</summary>
    public void Shutdown()
    {
        if (CurrentPage is PlayerViewModel player)
        {
            player.SaveProgressNow();
            player.Stop();
        }
    }
}
