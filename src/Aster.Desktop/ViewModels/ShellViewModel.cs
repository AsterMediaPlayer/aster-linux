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

    /// <summary>The user's libraries, fetched once per sign-in.</summary>
    Task<IReadOnlyList<BaseItem>> Libraries { get; }

    /// <summary>The song or book playing.</summary>
    Aster.Desktop.Audio.AudioSession Audio { get; }

    /// <summary>A book's page.</summary>
    void OpenBook(Aster.Core.Audio.Audiobook book);

    /// <summary>An author's books, or a series.</summary>
    void OpenShelf(Aster.Core.Audio.AudiobookShelf shelf);

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
    Search,
    Home,
    Movies,
    Shows,
    Anime,
    Music,
    Audiobooks,
    Library,
    Settings,
}

/// <summary>Why the user is signing out, which decides what sign-in starts with.</summary>
public enum SignOutKind
{
    /// <summary>Sign in again, or as someone else: the server's address stays filled in.</summary>
    SwitchUser,

    /// <summary>Another server: sign-in starts empty.</summary>
    ChangeServer,

    /// <summary>The server ended the session itself (401).</summary>
    Expired,
}

/// <summary>
/// The signed-in app: the sidebar and the pages opened from it, each over
/// the last, with Back going down the stack. The Apple app's sidebar also
/// has Upcoming, Live TV and Channels; they join as they're ported.
/// </summary>
public partial class ShellViewModel : ViewModelBase, INavigator, IDisposable
{
    private readonly Action<PlaybackQueueItem, Action<BaseItem?>?> play;
    private readonly Func<SignOutKind, Task> signOut;
    private readonly List<ViewModelBase> stack = [];
    private readonly Dictionary<AppTab, ViewModelBase> tabs = [];
    private AppTab tab = AppTab.Home;
    private bool userDataChanged;
    private readonly HashSet<ViewModelBase> refreshTabs = [];

    /// <param name="signOut">Signs out, saying why.</param>
    public ShellViewModel(JellyfinClient client, StoredSession session, Action<PlaybackQueueItem, Action<BaseItem?>?> play, Func<SignOutKind, Task> signOut)
    {
        Client = client;
        this.play = play;
        this.signOut = signOut;
        Anime = AnimeCatalog.FindAsync(client);
        Libraries = LoadLibrariesAsync(client);
        Audio = new Aster.Desktop.Audio.AudioSession(client);
        NowPlaying = new NowPlayingViewModel(this);
        UserName = session.UserName;
        ServerName = session.ServerName ?? session.ServerUrl.Host;
        Home = new HomeViewModel(this);
        tabs[AppTab.Home] = Home;
        CurrentPage = Home;
        UpdateTabs();
        // The Anime, Music and Audiobooks tabs show once it's known there's some.
        _ = Anime.ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateTabs));
        _ = Libraries.ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateTabs));
        // The settings that follow the user, as their other devices left them.
        _ = Aster.Core.SettingsSync.PullAsync(client);
    }

    public JellyfinClient Client { get; }
    public Task<AnimeCatalog> Anime { get; }
    public Task<IReadOnlyList<BaseItem>> Libraries { get; }
    public Aster.Desktop.Audio.AudioSession Audio { get; }

    /// <summary>The full player for the song or book, over the window.</summary>
    public NowPlayingViewModel NowPlaying { get; }

    private static async Task<IReadOnlyList<BaseItem>> LoadLibrariesAsync(JellyfinClient client)
    {
        try { return await client.UserViewsAsync(); }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException) { return []; }
    }

    [ObservableProperty] public partial IReadOnlyList<SidebarTab> Tabs { get; set; } = [];

    /// <summary>Settings, at the foot of the sidebar.</summary>
    [ObservableProperty] public partial SidebarTab? SettingsTab { get; set; }

    private void UpdateTabs()
    {
        var hasAnime = Anime.IsCompletedSuccessfully && Anime.Result.HasAnime;
        var libraries = Libraries.IsCompletedSuccessfully ? Libraries.Result : [];
        var hasMusic = libraries.Any(l => l.CollectionType == "music");
        var hasBooks = libraries.Any(l => l.CollectionType == "books");
        var all = new (AppTab Tab, string Title, string Glyph)[]
        {
            (AppTab.Search, "Search", ""),
            (AppTab.Home, "Home", ""),
            (AppTab.Movies, "Movies", ""),
            (AppTab.Shows, "TV Shows", ""),
            (AppTab.Anime, "Anime", ""),
            (AppTab.Music, "Music", ""),
            (AppTab.Audiobooks, "Audiobooks", ""),
            (AppTab.Library, "Library", ""),
        };
        Tabs = all
            .Where(t => t.Tab switch
            {
                AppTab.Anime => hasAnime,
                AppTab.Music => hasMusic,
                AppTab.Audiobooks => hasBooks,
                _ => true,
            })
            .Select(t => new SidebarTab(t.Title, t.Glyph, t.Tab == tab, new RelayCommand(() => ShowTab(t.Tab))))
            .ToList();
        SettingsTab = new SidebarTab("Settings", "", tab == AppTab.Settings, new RelayCommand(() => ShowTab(AppTab.Settings)));
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
                AppTab.Music => new MusicViewModel(this),
                AppTab.Audiobooks => new AudiobooksViewModel(this),
                AppTab.Library => new LibraryViewModel(this),
                AppTab.Search => new SearchViewModel(this),
                AppTab.Settings => new SettingsViewModel(this, UserName, ServerName, signOut),
                _ => Home,
            };
            tabs[chosen] = page;
        }
        tab = chosen;
        foreach (var opened in stack.Skip(1).Append(CurrentPage)) if (!tabs.ContainsValue(opened)) Close(opened);
        stack.Clear();
        CurrentPage = page;
        UpdateTabs();
        Shown();
        if (page is SearchViewModel search) search.RequestFocus();
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
        Push(item.Kind switch
        {
            ItemKind.Person => new PersonViewModel(this, item) { IsPushed = true },
            ItemKind.CollectionFolder or ItemKind.UserView or ItemKind.Folder when item.CollectionType == "music" => new MusicViewModel(this, item) { IsPushed = true },
            ItemKind.CollectionFolder or ItemKind.UserView or ItemKind.Folder when item.CollectionType == "books" => new AudiobooksViewModel(this, item) { IsPushed = true },
            ItemKind.MusicAlbum => new AlbumViewModel(this, item),
            ItemKind.Playlist when item.MediaType == "Audio" => new AlbumViewModel(this, item),
            ItemKind.MusicArtist => new ArtistViewModel(this, item),
            // A song opens its album, where it can be played in context.
            ItemKind.Audio when item.AlbumId is { } album => new AlbumViewModel(this, new BaseItem { Id = album, Name = item.Album, Type = "MusicAlbum" }),
            // Found by Search: the file as a book of its own.
            ItemKind.AudioBook => new AudiobookViewModel(this, Aster.Core.Audio.Audiobook.FromItem(item)),
            ItemKind.CollectionFolder or ItemKind.UserView or ItemKind.Folder or ItemKind.BoxSet or ItemKind.Playlist => new LibraryGridViewModel(this, item) { IsPushed = true },
            _ => new DetailViewModel(this, item, playOnOpen),
        });
    }

    public void OpenBook(Aster.Core.Audio.Audiobook book) => Push(new AudiobookViewModel(this, book));

    public void OpenShelf(Aster.Core.Audio.AudiobookShelf shelf) => Push(new AudiobookShelfViewModel(this, shelf) { IsPushed = true });

    private void Push(ViewModelBase page)
    {
        stack.Add(CurrentPage);
        CurrentPage = page;
    }

    /// <summary>A page gone for good lets go of what it watched (the players).</summary>
    private static void Close(ViewModelBase page)
    {
        if (page is IDisposable disposable) disposable.Dispose();
    }

    [RelayCommand]
    public void Back()
    {
        if (stack.Count == 0) return;
        Close(CurrentPage);
        CurrentPage = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        Shown();
    }


    public void Play(PlaybackQueueItem entry, Action<BaseItem?>? closed = null)
    {
        // A song or book playing pauses for the video, and waits in the bar.
        Audio.Pause();
        play(entry, last =>
        {
            // Watching changes resume points and watched states everywhere.
            userDataChanged = true;
            closed?.Invoke(last);
            Shown(alreadyRefreshed: closed is not null);
        });
    }

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
    private Task SignOutAsync() => signOut(SignOutKind.SwitchUser);

    public Task SessionExpiredAsync() => signOut(SignOutKind.Expired);

    /// <summary>Signing out: the song or book stops, its place saved.</summary>
    public void Dispose() => Audio.Dispose();
}
