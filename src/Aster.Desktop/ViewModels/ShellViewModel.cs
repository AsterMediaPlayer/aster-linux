using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using Aster.Core.Playback;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>What pages ask of the app: open a title, go back, play.</summary>
public interface INavigator
{
    JellyfinClient Client { get; }

    /// <summary>
    /// Opens a title's page over the current one. A stub item (id, name,
    /// type) is enough; the page loads the rest. With <paramref name="playOnOpen"/>
    /// it plays at once, the page behind the player.
    /// </summary>
    void Open(BaseItem item, bool playOnOpen = false);

    void Back();

    /// <summary>
    /// Plays over the whole window. <paramref name="closed"/> hears when the
    /// player closes, with the last title it played (an episode it moved on
    /// to by itself, say).
    /// </summary>
    void Play(PlaybackQueueItem entry, Action<BaseItem?>? closed = null);

    /// <summary>Something's watched or favourite state changed; pages refresh when shown again.</summary>
    void UserDataChanged();

    /// <summary>The server turned the session away (401): back to sign-in.</summary>
    Task SessionExpiredAsync();
}

/// <summary>A page that freshens what it shows when it comes back into view.</summary>
public interface IRefreshable
{
    void Refresh();
}

/// <summary>
/// The signed-in app: the sidebar and the pages opened from it, each over
/// the last, with Back going down the stack. The Apple app's sidebar has
/// Home, Movies, TV Shows, Anime and more; they join as they're ported.
/// </summary>
public partial class ShellViewModel : ViewModelBase, INavigator
{
    private readonly Action<PlaybackQueueItem, Action<BaseItem?>?> play;
    private readonly Func<bool, Task> signOut;
    private readonly List<ViewModelBase> stack = [];
    private bool userDataChanged;

    /// <param name="signOut">Signs out; true when the server ended the session itself.</param>
    public ShellViewModel(JellyfinClient client, StoredSession session, Action<PlaybackQueueItem, Action<BaseItem?>?> play, Func<bool, Task> signOut)
    {
        Client = client;
        this.play = play;
        this.signOut = signOut;
        UserName = session.UserName;
        ServerName = session.ServerName ?? session.ServerUrl.Host;
        Home = new HomeViewModel(this);
        CurrentPage = Home;
    }

    public JellyfinClient Client { get; }
    public HomeViewModel Home { get; }
    public string UserName { get; }
    public string ServerName { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    public partial ViewModelBase CurrentPage { get; set; }

    public bool CanGoBack => stack.Count > 0;

    public void Open(BaseItem item, bool playOnOpen = false)
    {
        stack.Add(CurrentPage);
        CurrentPage = new DetailViewModel(this, item, playOnOpen);
    }

    [RelayCommand]
    public void Back()
    {
        if (stack.Count == 0) return;
        CurrentPage = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        Shown();
    }

    [RelayCommand]
    private void ShowHome()
    {
        stack.Clear();
        CurrentPage = Home;
        Shown();
    }

    public void Play(PlaybackQueueItem entry, Action<BaseItem?>? closed = null) =>
        play(entry, last =>
        {
            // Watching changes resume points and watched states everywhere.
            userDataChanged = true;
            closed?.Invoke(last);
            Shown(alreadyRefreshed: closed is not null);
        });

    public void UserDataChanged() => userDataChanged = true;

    /// <summary>The page in view refreshes if something changed while it was away.</summary>
    private void Shown(bool alreadyRefreshed = false)
    {
        if (userDataChanged)
        {
            userDataChanged = false;
            // Home lists everything in progress, so it catches up when next shown.
            Home.RefreshLater();
            if (!alreadyRefreshed && CurrentPage != Home && CurrentPage is IRefreshable page) page.Refresh();
        }
        if (CurrentPage == Home) Home.Shown();
    }

    [RelayCommand]
    private Task SignOutAsync() => signOut(false);

    public Task SessionExpiredAsync() => signOut(true);
}
