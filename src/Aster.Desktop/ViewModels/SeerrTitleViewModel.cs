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
    public string? Detail => episodes is { } count ? $"{count} episode{(count == 1 ? "" : "s")}" : null;
    public string? StatusLabel => status.Label();
    public bool HasStatus => StatusLabel is not null;
    public bool CanRequest => status.CanRequest();
    [ObservableProperty] public partial bool IsChecked { get; set; }
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

    public SeerrTitleViewModel(INavigator navigator, SeerrTitle title, SeerrRequest? request = null)
    {
        this.navigator = navigator;
        this.title = title;
        this.request = request;
        _ = LoadAsync();
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
    public Uri? Backdrop => SeerrPages.Url(SeerrClient.Image(details?.BackdropPath ?? title.BackdropPath, "w1280"));
    public Uri? Poster => SeerrPages.Url(SeerrClient.Image(details?.PosterPath ?? title.PosterPath, "w342"));
    public string? Tagline => string.IsNullOrWhiteSpace(details?.Tagline) ? null : details.Tagline;
    public string? Overview => details?.Overview ?? title.Overview;
    public bool IsMovie => title.IsMovie;

    /// <summary>"2024 · Series · 2 h 46 min · Science Fiction, Adventure".</summary>
    public string Meta => string.Join(" · ", new[]
    {
        details?.Year ?? title.Year,
        title.IsMovie ? "Movie" : "Series",
        details?.Runtime is > 0 and var minutes ? (minutes >= 60 ? $"{minutes / 60} h {minutes % 60} min" : $"{minutes} min") : null,
        details?.Genres is { Count: > 0 } genres ? string.Join(", ", genres.Take(3).Select(g => g.Name)) : null,
        details?.Networks is { Count: > 0 } networks ? networks[0].Name : null,
    }.OfType<string>());

    private SeerrStatus Status => details?.MediaInfo?.Status ?? title.Status;
    public string? StatusLabel => Status.Label();
    public bool HasStatus => StatusLabel is not null;
    public bool CanRequest => Status.CanRequest() && navigator.Seerr.Account is not null;
    public bool InLibrary => (details?.MediaInfo ?? title.MediaInfo)?.JellyfinMediaId is { Length: > 0 } && navigator.Jellyfin is not null;
    public string? TrailerUrl => details?.Trailer?.Url;
    public bool HasTrailer => TrailerUrl is not null;
    public string RequestLabel => title.IsMovie ? "Request" : Seasons.Any(s => s.IsChecked) ? "Request Seasons" : "Request All Seasons";

    [ObservableProperty] public partial IReadOnlyList<SeerrSeasonChoice> Seasons { get; private set; } = [];
    public bool HasSeasons => Seasons.Count > 0;
    [ObservableProperty] public partial string? Message { get; private set; }
    [ObservableProperty] public partial bool IsBusy { get; private set; }

    private async Task LoadAsync()
    {
        if (navigator.Seerr.Account is not { } account) return;
        try
        {
            if (request is not null && me is null) me = await SeerrClient.MeAsync(account);
            details = await SeerrClient.DetailsAsync(title, account);
            if (details.MediaInfo is { } info) title = title with { MediaInfo = info };
            if (!title.IsMovie)
            {
                var show = new SeerrShow { Seasons = details.Seasons ?? [], MediaInfo = details.MediaInfo };
                Seasons = show.SeasonStatus().Select(s =>
                {
                    var choice = new SeerrSeasonChoice(s.Number, s.Episodes, s.Status);
                    choice.PropertyChanged += (_, _) => OnPropertyChanged(nameof(RequestLabel));
                    return choice;
                }).ToList();
            }
        }
        catch (SeerrException error) when (error.Failure == SeerrFailure.SignedOut) { navigator.Seerr.SessionEnded(); Message = error.Message; }
        catch (Exception error) when (error is SeerrException or HttpRequestException or TaskCanceledException) { Message = error.Message; }
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Asks Seerr for the film, or the seasons ticked (all those open, with none ticked).</summary>
    [RelayCommand]
    private async Task Request()
    {
        if (navigator.Seerr.Account is not { } account || IsBusy) return;
        var seasons = title.IsMovie ? null
            : (Seasons.Any(s => s.IsChecked) ? Seasons.Where(s => s.IsChecked && s.CanRequest) : Seasons.Where(s => s.CanRequest)).Select(s => s.Number).ToList();
        if (seasons is { Count: 0 })
        {
            Message = "Every season is already requested or there.";
            return;
        }
        IsBusy = true;
        try
        {
            await SeerrClient.RequestAsync(title, seasons, account);
            Message = title.IsMovie ? "Requested." : $"Requested {(seasons!.Count == 1 ? $"season {seasons[0]}" : $"{seasons!.Count} seasons")}.";
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

    [RelayCommand]
    private Task OpenInLibrary() => SeerrPages.OpenAsync(navigator, title);

    [RelayCommand]
    private void Back() => navigator.Back();
}
