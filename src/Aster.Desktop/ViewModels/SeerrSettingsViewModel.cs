using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using Aster.Core.Seerr;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// Settings › Requests: the Seerr sign-in, for Discover. Signed in on any
/// Aster app it's here already (synced through the server); else Seerr's
/// address, then whichever ways it lets people sign in: Quick Connect
/// (approved here when Seerr uses this same Jellyfin, nothing to type), the
/// media server's user and password, or a Seerr account of its own. A port
/// of the Apple app's Settings › Requests.
/// </summary>
public sealed partial class SeerrSettingsViewModel : ViewModelBase
{
    private readonly INavigator navigator;
    private CancellationTokenSource? quickConnect;

    public SeerrSettingsViewModel(INavigator navigator)
    {
        this.navigator = navigator;
        Address = navigator.Seerr.Stored?.Url.ToString().TrimEnd('/') ?? "";
        navigator.Seerr.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(string.Empty));
    }

    public bool IsSignedIn => navigator.Seerr.Account is not null;
    public bool NeedsSignIn => navigator.Seerr.NeedsSignIn;
    public bool ShowsSignIn => !IsSignedIn;
    public string SignedInLine => navigator.Seerr.Stored is { } account ? $"Signed in as {account.Name} at {account.Url.Host}{(account.Url.IsDefaultPort ? "" : $":{account.Url.Port}")}" : "";

    [ObservableProperty] public partial string Address { get; set; } = "";
    [ObservableProperty] public partial string UserName { get; set; } = "";
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial string Email { get; set; } = "";
    [ObservableProperty] public partial string LocalPassword { get; set; } = "";
    [ObservableProperty] public partial string? Message { get; private set; }
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    /// <summary>A Quick Connect code to approve in Jellyfin, when this app couldn't approve it itself.</summary>
    [ObservableProperty] public partial string? Code { get; private set; }

    /// <summary>What Seerr at the address offers, once found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Found), nameof(OffersMediaServer), nameof(OffersQuickConnect), nameof(OffersLocal), nameof(MediaServerName))]
    public partial SeerrClient.SignOptions? Options { get; private set; }

    public bool Found => Options is not null;
    public bool OffersMediaServer => Options?.SignsInWithJellyfinOrEmby == true;
    public bool OffersQuickConnect => Options?.MediaServerType == 2 && Options.MediaServerLogin != false;
    public bool OffersLocal => Options?.LocalLogin == true;
    public string MediaServerName => Options?.MediaServerType == 3 ? "Emby" : "Jellyfin";
    public bool HasCode => Code is not null;

    partial void OnAddressChanged(string value) => Options = null;

    private Uri? Typed => SeerrClient.Address(Address);

    /// <summary>Asks Seerr at the address how it lets people sign in.</summary>
    [RelayCommand]
    private Task Find() => RunAsync(async () =>
    {
        if (Typed is not { } typed) throw new SeerrException(SeerrFailure.Refused, "Enter Seerr's address, as 192.168.1.240:5055 or https://requests.example.com.");
        Options = await SeerrClient.SignInOptionsAsync(await SeerrClient.ReachableAsync(typed));
        if (!OffersMediaServer && !OffersLocal) Message = "This Seerr has no sign-in Aster can use.";
    });

    [RelayCommand]
    private Task SignInWithPassword() => SignInAsync(url => SeerrClient.SignInAsync(url, UserName.Trim(), Password));

    [RelayCommand]
    private Task SignInLocal() => SignInAsync(url => SeerrClient.SignInLocalAsync(url, Email.Trim(), LocalPassword));

    /// <summary>
    /// Quick Connect through Seerr's Jellyfin: the code approved here when
    /// it's this same Jellyfin (one press), else shown to approve there; then
    /// asked after every two seconds, five minutes at most, as long as Jellyfin keeps a code.
    /// </summary>
    [RelayCommand]
    private Task SignInWithQuickConnect() => RunAsync(async () =>
    {
        if (Typed is not { } typed) return;
        quickConnect?.Cancel();
        var cancel = quickConnect = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var url = await SeerrClient.ReachableAsync(typed);
        var (code, secret) = await SeerrClient.QuickConnectStartAsync(url, cancel.Token);
        var approved = false;
        if (navigator.Jellyfin is { IsJellyfin: true } jellyfin)
        {
            try { approved = await jellyfin.QuickConnectAuthorizeAsync(code, cancel.Token); }
            catch (Exception error) when (error is JellyfinException or HttpRequestException) { }
        }
        if (!approved) Code = code;
        try
        {
            while (!await SeerrClient.QuickConnectApprovedAsync(url, secret, cancel.Token))
                await Task.Delay(TimeSpan.FromSeconds(2), cancel.Token);
            Done(await SeerrClient.SignInWithQuickConnectAsync(url, secret, cancel.Token), typed);
        }
        catch (TaskCanceledException) when (cancel.IsCancellationRequested)
        {
            if (quickConnect == cancel) Message = "The code ran out. Try again.";
        }
        finally { Code = null; }
    });

    /// <summary>Sign Out pressed once: it asks to be pressed again (the sign-out reaches every device), for a few seconds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignOutLabel))]
    public partial bool ConfirmingSignOut { get; private set; }

    public string SignOutLabel => ConfirmingSignOut ? "Press Again to Sign Out of Seerr Everywhere" : "Sign Out of Seerr";

    [RelayCommand]
    private async Task SignOut()
    {
        if (!ConfirmingSignOut)
        {
            ConfirmingSignOut = true;
            await Task.Delay(TimeSpan.FromSeconds(5));
            ConfirmingSignOut = false;
            return;
        }
        ConfirmingSignOut = false;
        quickConnect?.Cancel();
        navigator.Seerr.SignOut();
        Message = "Signed out of Seerr.";
        Options = null;
    }

    private Task SignInAsync(Func<Uri, Task<SeerrAccount>> signIn) => RunAsync(async () =>
    {
        if (Typed is not { } typed) return;
        Done(await signIn(await SeerrClient.ReachableAsync(typed)), typed);
    });

    /// <summary>Signed in: kept with the address as typed (the one synced to the other devices).</summary>
    private void Done(SeerrAccount account, Uri typed)
    {
        navigator.Seerr.SignedIn(account with { Url = typed });
        Password = LocalPassword = "";
        Options = null;
        Message = $"Signed in to Seerr as {account.Name}.";
    }

    private async Task RunAsync(Func<Task> work)
    {
        if (IsBusy && quickConnect is null) return;
        IsBusy = true;
        Message = null;
        try { await work(); }
        catch (Exception error) when (error is SeerrException or HttpRequestException or TaskCanceledException or UriFormatException)
        {
            Message = error is TaskCanceledException ? "Seerr didn't answer." : error.Message;
        }
        finally { IsBusy = false; }
    }
}
