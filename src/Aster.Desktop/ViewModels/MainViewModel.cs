using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using Aster.Core.Playback;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aster.Desktop.ViewModels;

/// <summary>Which screen the window shows: sign-in, Home, or the player.</summary>
public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial ViewModelBase? CurrentPage { get; set; }

    private HomeViewModel? home;

    /// <param name="playItemId">A title to play straight away (<c>--play &lt;id&gt;</c>), for development.</param>
    /// <param name="maxBitrate">A quality cap for that title (<c>--max-bitrate</c>), to try converted streams.</param>
    /// <param name="startSeconds">Where to start that title (<c>--start</c>), instead of its resume point.</param>
    public MainViewModel(string? playItemId = null, int? maxBitrate = null, double? startSeconds = null)
    {
        if ((DevelopmentSession() ?? StoredSession.Load()) is { } session)
        {
            var client = new JellyfinClient(session.ServerUrl, session.AccessToken, session.UserId);
            ShowHome(client, session);
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
        var server = System.Environment.GetEnvironmentVariable("ASTER_SERVER");
        var token = System.Environment.GetEnvironmentVariable("ASTER_TOKEN");
        var user = System.Environment.GetEnvironmentVariable("ASTER_USER_ID");
        if (server is not null && token is not null && user is not null)
        {
            return new StoredSession { ServerUrl = new System.Uri(server), UserId = user, UserName = "Development", AccessToken = token };
        }
#endif
        return null;
    }

    private void ShowSignIn(string? message = null)
    {
        home = null;
        CurrentPage = new SignInViewModel(ShowHome) { Message = message };
    }

    private void ShowHome(JellyfinClient client, StoredSession session)
    {
        home = new HomeViewModel(client, session, SignOutAsync, item => Play(client, item));
        CurrentPage = home;
    }

    /// <summary>Plays a title from where it was left, as Home does.</summary>
    private async Task PlayByIdAsync(JellyfinClient client, string itemId, int? maxBitrate, double? startSeconds)
    {
        try { Play(client, await client.ItemAsync(itemId), maxBitrate, startSeconds); }
        catch (System.Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException) { }
    }

    private void Play(JellyfinClient client, BaseItem item, int? maxBitrate = null, double? startSeconds = null)
    {
        var start = startSeconds is { } seconds ? (long)(seconds * 10_000_000) : item.UserData?.PlaybackPositionTicks ?? 0;
        var entry = new PlaybackQueueItem { ItemId = item.Id, StartTicks = start, MaxBitrate = maxBitrate };
        CurrentPage = new PlayerViewModel(client, entry, () =>
        {
            CurrentPage = home;
            home?.Refresh();
        });
    }

    private async Task SignOutAsync(JellyfinClient client, bool expired)
    {
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
