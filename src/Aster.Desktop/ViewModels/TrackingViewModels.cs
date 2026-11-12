using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using Aster.Core.Tracking;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>One service on Settings ▸ Tracking: linked or not, and whether the server sends to it.</summary>
public sealed record TrackingRow(string Title, string Line, bool IsLinked, bool Sends, string SendLabel,
    IRelayCommand Link, IRelayCommand Unlink, IRelayCommand FlipSending)
{
    public bool IsUnlinked => !IsLinked;
}

public sealed record TrackingGroup(string Title, IReadOnlyList<TrackingRow> Rows, bool IsLast);

public sealed record TrackingSend(string Title, string Detail);

/// <summary>
/// Settings ▸ Tracking, with the server's Aster Discovery and Tracking
/// plugin: each service linked once on the server, for every app, which
/// sends what's watched or listened to, from any app. Link, Unlink, a
/// switch for sending, and the plugin's log of the last sends, which is how
/// people see it work. The Apple app's PluginTrackingSections.
/// </summary>
public sealed partial class TrackingPageViewModel : ViewModelBase
{
    private readonly INavigator navigator;
    private readonly JellyfinClient client;
    private readonly TrackingFeatures features;

    public TrackingPageViewModel(INavigator navigator, JellyfinClient client, TrackingFeatures features)
    {
        this.navigator = navigator;
        this.client = client;
        this.features = features;
        _ = LoadAsync();
    }

    [ObservableProperty] public partial bool IsLoading { get; set; } = true;
    [ObservableProperty] public partial IReadOnlyList<TrackingGroup> Groups { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<TrackingSend> Sends { get; set; } = [];
    [ObservableProperty] public partial string? Waiting { get; set; }
    [ObservableProperty] public partial string? Problem { get; set; }

    public bool HasSends => Sends.Count > 0;
    partial void OnSendsChanged(IReadOnlyList<TrackingSend> value) => OnPropertyChanged(nameof(HasSends));

    public async Task LoadAsync()
    {
        try
        {
            Show(await TrackingAccountsApi.AccountsAsync(client));
            Problem = null;
        }
        catch (Exception error) when (error is TrackingException or System.Net.Http.HttpRequestException or TimeoutException or JellyfinException)
        {
            Problem = $"Your server's linked accounts couldn't be read ({error.Message}).";
        }
        IsLoading = false;
    }

    private void Show(TrackedAccounts accounts)
    {
        // The services the server offers (and has an app for), by group, in a fixed order.
        var offered = Enum.GetValues<TrackedService>()
            .Where(s => features.Services.Contains(s.Key(), StringComparer.OrdinalIgnoreCase) && accounts.Account(s)?.Available != false)
            .ToList();
        var groups = TrackedServices.Groups.Select(title => (title, services: offered.Where(s => s.Group() == title).ToList()))
            .Where(g => g.services.Count > 0).ToList();
        Groups = groups.Select((g, i) => new TrackingGroup(g.title, g.services.Select(s => Row(s, accounts.Account(s))).ToList(), i == groups.Count - 1)).ToList();
        Sends = (accounts.Log ?? []).Take(8).Select(entry => new TrackingSend(entry.Title ?? "Something",
            string.Join(" · ", new[] { entry.Service, entry.Result, TrackingAccountsApi.Date(entry.At)?.ToLocalTime().ToString("d MMM, HH:mm", CultureInfo.CurrentCulture) }.OfType<string>())))
            .ToList();
        Waiting = accounts.Waiting is > 0 and var waiting ? $"{waiting} waiting to be tried again." : null;
    }

    private TrackingRow Row(TrackedService service, TrackedAccount? account) => new(
        service.Title(),
        account is { IsLinked: true } linked ? LinkedLine(linked) : "Not linked",
        account?.IsLinked == true,
        account?.Scrobble ?? true,
        $"Send what I {service.Verb()}",
        new RelayCommand(() => navigator.Push(new TrackingLinkViewModel(navigator, client, service, () => _ = LoadAsync()))),
        new AsyncRelayCommand(() => Do(() => TrackingAccountsApi.UnlinkAsync(client, service))),
        new AsyncRelayCommand(() => Do(() => TrackingAccountsApi.SetScrobbleAsync(client, service, !(account?.Scrobble ?? true)))));

    /// <summary>"Linked as Name", and when AniList's link needs doing again.</summary>
    private static string LinkedLine(TrackedAccount account)
    {
        var line = account.Name is { } name ? $"Linked as {name}" : "Linked";
        if (TrackingAccountsApi.Date(account.Expires) is { } expires)
        {
            var day = expires.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
            line += expires < DateTimeOffset.UtcNow.AddDays(30) ? $" · link again by {day}" : $" · until {day}";
        }
        return line;
    }

    private async Task Do(Func<Task> change)
    {
        try
        {
            await change();
            await LoadAsync();
        }
        catch (Exception error) when (error is TrackingException or System.Net.Http.HttpRequestException or TimeoutException or JellyfinException)
        {
            Problem = error.Message;
        }
    }

    [RelayCommand] private void Back() => navigator.Back();
}

/// <summary>
/// Linking one service: a code to enter on its site, an approval to give
/// there, or a code to paste back, while the server waits on the service and
/// the page asks every few seconds whether it's done. The Apple app's
/// TrackingLinkSheet, without its QR code.
/// </summary>
public sealed partial class TrackingLinkViewModel : ViewModelBase
{
    private readonly INavigator navigator;
    private readonly JellyfinClient client;
    private readonly TrackedService service;
    private readonly Action linked;
    private CancellationTokenSource? running;

    public TrackingLinkViewModel(INavigator navigator, JellyfinClient client, TrackedService service, Action linked)
    {
        this.navigator = navigator;
        this.client = client;
        this.service = service;
        this.linked = linked;
        _ = RunAsync();
    }

    public string Title => $"Link {service.Title()}";
    public string ServiceTitle => service.Title();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCode), nameof(IsApprove), nameof(IsPaste), nameof(Address), nameof(Code), nameof(IsWaiting))]
    public partial TrackedLink? Link { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    public partial bool Expired { get; set; }

    [ObservableProperty] public partial string? Problem { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial string Pasted { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial bool Working { get; set; }

    public bool IsPaste => Link?.Paste == true;
    public bool IsCode => Link is { Paste: not true, Code: not null };
    public bool IsApprove => Link is { Paste: not true, Code: null };
    public bool IsWaiting => (IsCode || IsApprove) && !Expired;
    public string? Code => Link?.Code;
    public string Address => (Link?.Url ?? "").Replace("https://", "", StringComparison.Ordinal);

    public string PasteHelp => service == TrackedService.ListenBrainz
        ? "Open ListenBrainz, copy your user token, and paste it here."
        : $"Open {service.Title()}, sign in and approve Aster, then paste the code it shows here.";

    public string ApproveHelp => $"Open {service.Title()} and approve Aster. {service.Title()} may show an error page afterwards. Ignore it and come back here.";

    /// <summary>Starts linking, then (for the code and approve flows) asks every few seconds whether it's done.</summary>
    private async Task RunAsync()
    {
        running?.Cancel();
        var cancel = running = new CancellationTokenSource();
        Expired = false;
        Problem = null;
        Link = null;
        try
        {
            var started = await TrackingAccountsApi.StartLinkAsync(client, service, cancel.Token);
            Link = started;
            if (started.Paste == true) return;
            var interval = TimeSpan.FromSeconds(Math.Max(started.Interval ?? 5, 3));
            var deadline = DateTimeOffset.UtcNow.AddSeconds(started.ExpiresIn ?? 600);
            while (!cancel.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(interval, cancel.Token);
                if (await CheckAsync(cancel.Token)) return;
            }
            if (!cancel.IsCancellationRequested) Expired = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is TrackingException or System.Net.Http.HttpRequestException or TimeoutException or JellyfinException)
        {
            Problem = error.Message;
        }
    }

    /// <summary>One check for the code and approve flows; true once it's done (or ran out).</summary>
    private async Task<bool> CheckAsync(CancellationToken ct)
    {
        try
        {
            var (result, message) = await TrackingAccountsApi.CompleteAsync(client, service, ct: ct);
            switch (result)
            {
                case TrackedLinkResult.Linked:
                    Done();
                    return true;
                case TrackedLinkResult.Expired:
                    Expired = true;
                    return true;
                case TrackedLinkResult.Failed:
                    Problem = message;
                    return false;
            }
        }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or TimeoutException) { }
        return false;
    }

    private bool CanSubmit() => !Working && !string.IsNullOrWhiteSpace(Pasted);

    /// <summary>The pasted code or token, for the paste flows.</summary>
    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        Working = true;
        try
        {
            var (result, message) = await TrackingAccountsApi.CompleteAsync(client, service, Pasted.Trim());
            switch (result)
            {
                case TrackedLinkResult.Linked: Done(); break;
                case TrackedLinkResult.Waiting: Problem = "Not linked yet. Try again."; break;
                case TrackedLinkResult.Expired: Expired = true; break;
                default: Problem = message; break;
            }
        }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or TimeoutException)
        {
            Problem = error.Message;
        }
        finally { Working = false; }
    }

    private void Done()
    {
        Aster.Core.Diagnostics.DebugLog.Note("Tracking", $"Linked {service.Key()}");
        linked();
        Leave();
        navigator.Back();
    }

    /// <summary>The service's page, in the browser: with the code in the address where it has one.</summary>
    [RelayCommand]
    private void Open()
    {
        if ((Link?.CompleteUrl ?? Link?.Url) is not { Length: > 0 } url) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { Problem = "Couldn't open a browser."; }
    }

    [RelayCommand] private Task TryAgain() => RunAsync();

    [RelayCommand]
    private void Cancel()
    {
        Leave();
        navigator.Back();
    }

    /// <summary>The page has gone: no more asking.</summary>
    public void Leave() => running?.Cancel();
}
