using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core.Seerr;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>A season of a show to request: where it stands, and whether it's ticked for the request.</summary>
public sealed partial class SeerrSeasonChoice(int number, int? episodes, SeerrStatus status) : ObservableObject
{
    public int Number => number;
    public string Title => $"Season {number}";
    public bool CanRequest => status.CanRequest();
    /// <summary>An open season's episodes; another's status ("Available", "Requested").</summary>
    public string Detail => CanRequest ? episodes is { } count ? $"{count} episode{(count == 1 ? "" : "s")}" : " " : status.Label() ?? "";
    [ObservableProperty] public partial bool IsChecked { get; set; }
}

/// <summary>One of the Details: "RELEASE", "October 9, 1993".</summary>
public sealed record SeerrFact(string Label, string Value);

/// <summary>A cast member: their photo in a circle, their name and part.</summary>
public sealed record SeerrCastMember(string Name, string? Character, Uri? Photo)
{
    public bool HasCharacter => !string.IsNullOrEmpty(Character);
}

/// <summary>
/// A film or show the library hasn't got, from Seerr: its backdrop, details
/// and overview, its trailer, and Request (the seasons ticked, for a show).
/// A port of the Apple app's SeerrTitlePage.
/// </summary>
public sealed partial class SeerrTitleViewModel : ViewModelBase
{
    private readonly INavigator navigator;
    private SeerrTitle title;
    private SeerrDetails? details;
    /// <summary>The request this page was opened from, while it waits for approval.</summary>
    private SeerrRequest? request;
    private SeerrUser? me;
    private readonly bool prefer4k;

    /// <param name="prefer4k">Opened to ask for the 4K copy (from the library's page): a 4K server, or 4K, picked to start.</param>
    public SeerrTitleViewModel(INavigator navigator, SeerrTitle title, SeerrRequest? request = null, bool prefer4k = false)
    {
        this.navigator = navigator;
        this.title = title;
        this.request = request;
        this.prefer4k = wants4k = prefer4k;
        _ = LoadAsync();
    }

    // ---- Where a request goes (whoever manages requests chooses) ----

    private IReadOnlyList<SeerrService> services = [];
    private SeerrServiceChoices? serviceChoices;
    private SeerrRequestOptions? options;

    /// <summary>Server, Quality, Folder and (shows) Language, as menus, starting on Seerr's defaults (its anime ones for anime).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRequestOptions))]
    public partial IReadOnlyList<FilterChip> RequestOptions { get; private set; } = [];
    /// <summary>Shown while there's something to ask for, or a 4K copy to switch to (a film already here may not be in 4K).</summary>
    public bool HasRequestOptions => RequestOptions.Count > 0 && (CanRequest || offers4k || services.Any(s => s.Is4k == true));

    /// <summary>
    /// The servers, a 4K one among them for whoever may ask for 4K (its
    /// requests go to that Radarr or Sonarr, and the 4K copy's status is
    /// the one that counts). Seerr's default, not a 4K one, to start
    /// (its 4K default, opened to ask for 4K).
    /// </summary>
    private async Task LoadOptionsAsync(SeerrAccount account, int? serverId = null)
    {
        var kind = title.IsMovie ? SeerrKind.Movie : SeerrKind.Tv;
        if (services.Count == 0)
            services = (await SeerrClient.ServicesAsync(kind, account)).Where(s => s.Is4k != true || me?.Requests4k(title.IsMovie) == true).ToList();
        var server = services.FirstOrDefault(s => s.Id == serverId)
            ?? (prefer4k ? services.FirstOrDefault(s => s.IsDefault == true && s.Is4k == true) ?? services.FirstOrDefault(s => s.Is4k == true) : null)
            ?? services.FirstOrDefault(s => s.IsDefault == true && s.Is4k != true) ?? services.FirstOrDefault(s => s.Is4k != true) ?? services.FirstOrDefault();
        if (server is null) return;
        serviceChoices = await SeerrClient.ServiceChoicesAsync(kind, server.Id, account);
        options = SeerrRequestOptions.Defaults(server, details?.IsAnime == true);
        ShowOptions();
        Show4kChange();
    }

    /// <summary>"Radarr 4K", or "Radarr (4K)" when its name doesn't say.</summary>
    private static string ServerName(SeerrService server) =>
        server.Is4k == true && !server.Name.Contains("4k", StringComparison.OrdinalIgnoreCase) ? $"{server.Name} (4K)" : server.Name;

    /// <summary>Asking for the 4K copy: a 4K server chosen, or (for one who can't choose servers) 4K picked.</summary>
    private bool Is4k => options is { } chosen ? services.FirstOrDefault(s => s.Id == chosen.ServerId)?.Is4k == true : wants4k;
    private bool wants4k;
    /// <summary>Whether Seerr has a 4K server for this kind, for one who can't see its servers.</summary>
    private bool offers4k;

    /// <summary>The status, seasons and buttons again, for the copy (4K or not) now asked for.</summary>
    private void Show4kChange()
    {
        BuildSeasons();
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(CanRequest));
        OnPropertyChanged(nameof(HasRequestOptions));
        OnPropertyChanged(nameof(RequestLabel));
    }

    /// <summary>For one who may ask for 4K but not choose servers: Standard or 4K.</summary>
    private void Show4kChoice()
    {
        RequestOptions =
        [
            new(wants4k ? "4K" : "Standard", false, Choices:
            [
                new TrackChoice(0, "Standard", !wants4k, new RelayCommand(() => { wants4k = false; Show4kChoice(); Show4kChange(); })),
                new TrackChoice(1, "4K", wants4k, new RelayCommand(() => { wants4k = true; Show4kChoice(); Show4kChange(); })),
            ]),
        ];
    }

    private void ShowOptions()
    {
        if (options is not { } chosen || serviceChoices is not { } offered) return;
        var server = services.FirstOrDefault(s => s.Id == chosen.ServerId);
        var chips = new List<FilterChip>
        {
            new(server is null ? "Server" : ServerName(server), false, Choices: services.Select(s => new TrackChoice(s.Id, ServerName(s), s.Id == chosen.ServerId,
                new RelayCommand(() => { if (navigator.Seerr.Account is { } account) _ = LoadOptionsAsync(account, s.Id); }))).ToList()),
        };
        if (offered.Profiles is { Count: > 0 } profiles)
            chips.Add(new(profiles.FirstOrDefault(p => p.Id == chosen.ProfileId)?.Name ?? "Quality", false, Choices: profiles.Select(p =>
                new TrackChoice(p.Id, p.Name, p.Id == chosen.ProfileId, new RelayCommand(() => { options = chosen with { ProfileId = p.Id }; ShowOptions(); }))).ToList()));
        if (offered.RootFolders is { Count: > 0 } folders)
            chips.Add(new(chosen.RootFolder ?? "Folder", false, Choices: folders.Select((f, i) =>
                new TrackChoice(i, f.FreeSpace is { } free ? $"{f.Path} ({free / 1_000_000_000:N0} GB free)" : f.Path, f.Path == chosen.RootFolder,
                    new RelayCommand(() => { options = chosen with { RootFolder = f.Path }; ShowOptions(); }))).ToList()));
        if (offered.LanguageProfiles is { Count: > 0 } languages)
            chips.Add(new(languages.FirstOrDefault(l => l.Id == chosen.LanguageProfileId)?.Name ?? "Language", false, Choices: languages.Select(l =>
                new TrackChoice(l.Id, l.Name, l.Id == chosen.LanguageProfileId, new RelayCommand(() => { options = chosen with { LanguageProfileId = l.Id }; ShowOptions(); }))).ToList()));
        RequestOptions = chips;
    }

    // ---- A request waiting for approval ----

    /// <summary>"Requested by Phillip · Pending Approval", for a request opened from Requests.</summary>
    public string? RequestLine => request is { } asked
        ? string.Join(" · ", new[] { asked.RequestedBy?.DisplayName is { } who ? $"Requested by {who}" : null, asked.StateLabel }.OfType<string>())
        : null;
    public bool HasRequestLine => RequestLine is not null;
    /// <summary>Approve and Decline: for whoever manages Seerr's requests (its admin, or MANAGE_REQUESTS).</summary>
    public bool CanDecide => request is { IsPending: true } && me?.ManagesRequests == true;
    /// <summary>Cancel: one's own request, while it waits.</summary>
    public bool CanCancel => request is { IsPending: true } asked && me is { } user && asked.RequestedBy?.Id == user.Id;

    [RelayCommand]
    private Task Approve() => DecideAsync(SeerrClient.ApproveAsync, "Approved.");

    [RelayCommand]
    private Task Decline() => DecideAsync(SeerrClient.DeclineAsync, "Declined.");

    [RelayCommand]
    private Task CancelRequest() => DecideAsync(SeerrClient.CancelAsync, "Request cancelled.");

    private async Task DecideAsync(Func<SeerrRequest, SeerrAccount, System.Threading.CancellationToken, Task> act, string done)
    {
        if (navigator.Seerr.Account is not { } account || request is not { } asked || IsBusy) return;
        IsBusy = true;
        try
        {
            await act(asked, account, default);
            Message = done;
            // Done with: the buttons go, and the page shows where the title stands now.
            request = null;
            await LoadAsync();
        }
        catch (SeerrException error) when (error.Failure == SeerrFailure.SignedOut) { navigator.Seerr.SessionEnded(); Message = error.Message; }
        catch (Exception error) when (error is SeerrException or HttpRequestException or TaskCanceledException) { Message = error.Message; }
        finally { IsBusy = false; }
    }

    public string Title => details?.DisplayTitle ?? title.DisplayTitle;
    public Uri? Backdrop => SeerrPages.Url(SeerrClient.Image(details?.BackdropPath ?? title.BackdropPath, "original"));
    /// <summary>No backdrop: the poster stands in, blurred, and beside the name.</summary>
    public bool PosterOnly => (details?.BackdropPath ?? title.BackdropPath) is null;
    public Uri? Poster => SeerrPages.Url(SeerrClient.Image(details?.PosterPath ?? title.PosterPath, "w342"));
    public string? Tagline => string.IsNullOrWhiteSpace(details?.Tagline) ? null : details.Tagline;
    public string? Overview => details?.Overview ?? title.Overview;
    public bool IsMovie => title.IsMovie;

    /// <summary>"1993 · Fantasy, Animation, Family · 1 hr 16 min", as the Apple app's.</summary>
    public string Meta => string.Join("  ·  ", new[]
    {
        details?.Year ?? title.Year,
        details?.Genres is { Count: > 0 } genres ? string.Join(", ", genres.Take(3).Select(g => g.Name)) : null,
        details?.Networks is { Count: > 0 } networks ? networks[0].Name : null,
        title.IsMovie && details?.Runtime is > 0 and var minutes ? (minutes >= 60 ? $"{minutes / 60} hr {minutes % 60} min" : $"{minutes} min") : null,
        details?.NumberOfSeasons is > 0 and var count ? (count == 1 ? "1 Season" : $"{count} Seasons") : null,
        details?.Status is { Length: > 0 } state && state != "Released" ? state : null,
    }.OfType<string>());

    /// <summary>"Premieres January 10, 2027", for what isn't out yet.</summary>
    public string? PremiereLine => details?.Date is { } date && date > DateTime.Now
        ? $"{(title.IsMovie ? "Releases" : "Premieres")} {date:MMMM d, yyyy}" : null;

    /// <summary>Where it stands, for the copy asked for: the 4K copy's, with a 4K server chosen.</summary>
    private SeerrMediaInfo? Info => (details?.MediaInfo ?? title.MediaInfo) is { } info && Is4k ? info.For4k() : details?.MediaInfo ?? title.MediaInfo;
    private SeerrStatus Status => Is4k ? Info?.Status ?? SeerrStatus.Unknown : details?.MediaInfo?.Status ?? title.Status;
    public string? StatusLabel => Status.Label() is { } label && Is4k ? $"4K {label}" : Status.Label();
    public bool HasStatus => StatusLabel is not null && !CanRequest;
    public bool IsAvailable => Status == SeerrStatus.Available;
    public bool CanRequest => Status.CanRequest() && navigator.Seerr.Account is not null
        && (title.IsMovie || Seasons.Count == 0 || Seasons.Any(s => s.CanRequest));

    /// <summary>The Details: release, status, network, studio, director, and so on, as far as they're known.</summary>
    public IReadOnlyList<SeerrFact> Facts
    {
        get
        {
            if (details is not { } d) return [];
            var facts = new List<SeerrFact>();
            if (d.Original is { Length: > 0 } original && original != d.DisplayTitle) facts.Add(new("Original Title", original));
            if (d.Date is { } date) facts.Add(new(title.IsMovie ? "Release" : "Premiere", date.ToString("MMMM d, yyyy")));
            if (d.Status is { Length: > 0 } state) facts.Add(new("Status", state));
            if (d.Networks is { Count: > 0 } networks) facts.Add(new(networks.Count == 1 ? "Network" : "Networks", ShortList(networks.Select(n => n.Name).ToList())));
            if (d.ProductionCompanies is { Count: > 0 } studios) facts.Add(new("Studio", string.Join(", ", studios.Take(3).Select(c => c.Name))));
            if (d.Makers is { Count: > 0 } makers) facts.Add(new(title.IsMovie ? "Director" : "Created by", string.Join(", ", makers.Take(3))));
            if (d.NumberOfEpisodes is > 0 and var episodes) facts.Add(new("Episodes", episodes.ToString()));
            if (d.Genres is { Count: > 0 } genres) facts.Add(new("Genres", string.Join(", ", genres.Select(g => g.Name))));
            if (d.Keywords is { Count: > 0 } keywords) facts.Add(new("Keywords", string.Join(", ", keywords.Take(8).Select(k => k.Name))));
            return facts;
        }
    }
    public bool HasDetails => details is not null;

    /// <summary>The first three, and how many more: a show on every local station in Japan lists dozens.</summary>
    private static string ShortList(IReadOnlyList<string> names) =>
        names.Count > 4 ? $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more" : string.Join(", ", names);

    /// <summary>TMDB's score, "79%".</summary>
    public string? Score => details?.VoteAverage is > 0 and var vote ? $"{Math.Round(vote * 10):0}%" : null;
    public bool HasScore => Score is not null;

    public IReadOnlyList<SeerrCastMember> Cast => (details?.Credits?.Cast ?? []).Take(20)
        .Select(p => new SeerrCastMember(p.Name, p.Character, SeerrPages.Url(SeerrClient.Image(p.ProfilePath, "w185")))).ToList();
    public bool HasCast => Cast.Count > 0;
    public bool InLibrary => (details?.MediaInfo ?? title.MediaInfo)?.JellyfinMediaId is { Length: > 0 } && navigator.Jellyfin is not null;
    public string? TrailerUrl => details?.Trailer?.Url;
    public bool HasTrailer => TrailerUrl is not null;
    /// <summary>"Request", "Request All Seasons", "Request 2 Seasons"; "in 4K" with 4K asked for.</summary>
    public string RequestLabel
    {
        get
        {
            var chosen = Seasons.Count(s => s.IsChecked && s.CanRequest);
            var label = title.IsMovie || Seasons.Count == 0 ? "Request"
                : chosen == Seasons.Count(s => s.CanRequest) ? "Request All Seasons"
                : chosen == 1 ? "Request 1 Season" : $"Request {chosen} Seasons";
            return Is4k ? $"{label} in 4K" : label;
        }
    }
    /// <summary>A show with none of its seasons picked has nothing to ask for.</summary>
    public bool CanSend => !IsBusy && (title.IsMovie || Seasons.Count == 0 || Seasons.Any(s => s.IsChecked && s.CanRequest));

    [ObservableProperty] public partial IReadOnlyList<SeerrSeasonChoice> Seasons { get; private set; } = [];
    public bool HasSeasons => Seasons.Count > 0;
    [ObservableProperty] public partial string? Message { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    public partial bool IsBusy { get; private set; }

    /// <summary>The seasons, as the copy asked for (4K or not) stands; the open ones picked.</summary>
    private void BuildSeasons()
    {
        if (title.IsMovie || details is null) return;
        var show = new SeerrShow { Seasons = details.Seasons ?? [], MediaInfo = Info };
        Seasons = show.SeasonStatus().Select(s =>
        {
            // Picked to start only when there's a Request to send them with.
            var choice = new SeerrSeasonChoice(s.Number, s.Episodes, s.Status) { IsChecked = s.Status.CanRequest() && Status.CanRequest() };
            choice.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(RequestLabel)); OnPropertyChanged(nameof(CanSend)); };
            return choice;
        }).ToList();
    }

    private async Task LoadAsync()
    {
        if (navigator.Seerr.Account is not { } account) return;
        try
        {
            me ??= await SeerrClient.MeAsync(account);
            details = await SeerrClient.DetailsAsync(title, account);
            if (details.MediaInfo is { } info) title = title with { MediaInfo = info };
            BuildSeasons();
        }
        catch (SeerrException error) when (error.Failure == SeerrFailure.SignedOut) { navigator.Seerr.SessionEnded(); Message = error.Message; }
        catch (Exception error) when (error is SeerrException or HttpRequestException or TaskCanceledException) { Message = error.Message; }
        OnPropertyChanged(string.Empty);
        // Whoever manages requests chooses where one goes (a 4K server among
        // them); one who may only ask for 4K picks Standard or 4K. A fault
        // there leaves Seerr's defaults.
        if (navigator.Seerr.Account is not { } asker || options is not null || RequestOptions.Count > 0) return;
        try
        {
            if (me?.ManagesRequests == true) await LoadOptionsAsync(asker);
            else if (me?.Requests4k(title.IsMovie) == true)
            {
                var settings = await SeerrClient.SignInOptionsAsync(asker.Url);
                offers4k = title.IsMovie ? settings.Movie4kEnabled == true : settings.Series4kEnabled == true;
                if (offers4k) Show4kChoice();
            }
        }
        catch (Exception error) when (error is SeerrException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
        OnPropertyChanged(nameof(HasRequestOptions));
    }

    /// <summary>Asks Seerr for the film, or the seasons ticked (all those open, with none ticked).</summary>
    [RelayCommand]
    private async Task Request()
    {
        if (navigator.Seerr.Account is not { } account || IsBusy) return;
        var seasons = title.IsMovie ? null : Seasons.Where(s => s.IsChecked && s.CanRequest).Select(s => s.Number).ToList();
        if (seasons is { Count: 0 } && Seasons.Count > 0)
        {
            Message = "Pick a season to ask for.";
            return;
        }
        var body = options?.Body() ?? new Dictionary<string, object>();
        if (Is4k) body["is4k"] = true;
        IsBusy = true;
        try
        {
            await SeerrClient.RequestAsync(title, seasons, account, body);
            var what = title.IsMovie || seasons is null or { Count: 0 } ? "Requested" : $"Requested {(seasons.Count == 1 ? $"season {seasons[0]}" : $"{seasons.Count} seasons")}";
            Message = Is4k ? $"{what} in 4K." : $"{what}.";
            await LoadAsync();
        }
        catch (SeerrException error) when (error.Failure == SeerrFailure.SignedOut) { navigator.Seerr.SessionEnded(); Message = error.Message; }
        catch (Exception error) when (error is SeerrException or HttpRequestException or TaskCanceledException) { Message = error.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>The trailer, in the browser (YouTube).</summary>
    [RelayCommand]
    private void Trailer()
    {
        if (TrailerUrl is not { } url) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { Message = "Couldn't open a browser for the trailer."; }
    }

    /// <summary>The library's page: opened from it (for 4K), back to it.</summary>
    [RelayCommand]
    private async Task OpenInLibrary()
    {
        if (prefer4k) navigator.Back();
        else await SeerrPages.OpenAsync(navigator, title);
    }

    [RelayCommand]
    private void Back() => navigator.Back();
}
