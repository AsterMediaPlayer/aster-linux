using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>Which libraries hold anime, found once per sign-in.</summary>
    Task<AnimeCatalog> Anime { get; }

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

/// <summary>A tab in the sidebar.</summary>
public sealed record SidebarTab(string Title, string Glyph, bool IsSelected, IRelayCommand Select);

public enum AppTab
{
    Home,
    Movies,
    Shows,
    Anime,
    Library,
}

/// <summary>
/// The signed-in app: the sidebar and the pages opened from it, each over
/// the last, with Back going down the stack. The Apple app's sidebar also
/// has Music, Audiobooks, Upcoming, Live TV, Channels, Settings and Search;
/// they join as they're ported.
/// </summary>
public partial class ShellViewModel : ViewModelBase, INavigator
{
    private readonly Action<PlaybackQueueItem, Action<BaseItem?>?> play;
    private readonly Func<bool, Task> signOut;
    private readonly List<ViewModelBase> stack = [];
    private readonly Dictionary<AppTab, ViewModelBase> tabs = [];
    private AppTab tab = AppTab.Home;
    private bool userDataChanged;
    private readonly HashSet<ViewModelBase> refreshTabs = [];

    /// <param name="signOut">Signs out; true when the server ended the session itself.</param>
    public ShellViewModel(JellyfinClient client, StoredSession session, Action<PlaybackQueueItem, Action<BaseItem?>?> play, Func<bool, Task> signOut)
    {
        Client = client;
        this.play = play;
        this.signOut = signOut;
        Anime = AnimeCatalog.FindAsync(client);
        UserName = session.UserName;
        ServerName = session.ServerName ?? session.ServerUrl.Host;
        Home = new HomeViewModel(this);
        tabs[AppTab.Home] = Home;
        CurrentPage = Home;
        UpdateTabs();
        // The Anime tab shows once it's known there's anime.
        _ = Anime.ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateTabs));
    }

    public JellyfinClient Client { get; }
    public Task<AnimeCatalog> Anime { get; }

    [ObservableProperty] public partial IReadOnlyList<SidebarTab> Tabs { get; set; } = [];

    private void UpdateTabs()
    {
        var hasAnime = Anime.IsCompletedSuccessfully && Anime.Result.HasAnime;
        var all = new (AppTab Tab, string Title, string Glyph)[]
        {
            (AppTab.Home, "Home", ""),
            (AppTab.Movies, "Movies", ""),
            (AppTab.Shows, "TV Shows", ""),
            (AppTab.Anime, "Anime", ""),
            (AppTab.Library, "Library", ""),
        };
        Tabs = all
            .Where(t => t.Tab != AppTab.Anime || hasAnime)
            .Select(t => new SidebarTab(t.Title, t.Glyph, t.Tab == tab, new RelayCommand(() => ShowTab(t.Tab))))
            .ToList();
    }

    /// <summary>A tab's page, from the top: made the first time, kept after.</summary>
    public void ShowTab(AppTab chosen)
    {
        if (!tabs.TryGetValue(chosen, out var page))
        {
            page = chosen switch
            {
                AppTab.Movies => new TypeGridViewModel(this, ItemKind.Movie),
                AppTab.Shows => new TypeGridViewModel(this, ItemKind.Series),
                AppTab.Anime => new AnimeViewModel(this),
                AppTab.Library => new LibraryViewModel(this),
                _ => Home,
            };
            tabs[chosen] = page;
        }
        tab = chosen;
        stack.Clear();
        CurrentPage = page;
        UpdateTabs();
        Shown();
    }
    public HomeViewModel Home { get; }
    public string UserName { get; }
    public string ServerName { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    public partial ViewModelBase CurrentPage { get; set; }

    public bool CanGoBack => stack.Count > 0;

    /// <summary>A title's page; a library, collection or playlist opens as a grid of what's in it, and a person as their filmography.</summary>
    public void Open(BaseItem item, bool playOnOpen = false)
    {
        stack.Add(CurrentPage);
        CurrentPage = item.Kind switch
        {
            ItemKind.Person => new PersonViewModel(this, item) { IsPushed = true },
            ItemKind.CollectionFolder or ItemKind.UserView or ItemKind.Folder or ItemKind.BoxSet or ItemKind.Playlist => new LibraryGridViewModel(this, item) { IsPushed = true },
            _ => new DetailViewModel(this, item, playOnOpen),
        };
    }

    [RelayCommand]
    public void Back()
    {
        if (stack.Count == 0) return;
        CurrentPage = stack[^1];
        stack.RemoveAt(stack.Count - 1);
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
            // The tabs list what's in progress, so they catch up when next shown.
            Home.RefreshLater();
            foreach (var other in tabs.Values) if (other != CurrentPage) refreshTabs.Add(other);
            if (!alreadyRefreshed && CurrentPage != Home && CurrentPage is IRefreshable page) page.Refresh();
        }
        if (CurrentPage == Home) Home.Shown();
        else if (CurrentPage is IRefreshable page && tabs.ContainsValue(CurrentPage) && refreshTabs.Remove(CurrentPage)) page.Refresh();
    }

    [RelayCommand]
    private Task SignOutAsync() => signOut(false);

    public Task SessionExpiredAsync() => signOut(true);
}
