using System;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

public partial class SignInViewModel : ViewModelBase
{
    private readonly Action<JellyfinClient, StoredSession> signedIn;

    public SignInViewModel(Action<JellyfinClient, StoredSession> signedIn) => this.signedIn = signedIn;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string ServerAddress { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string UserName { get; set; } = "";

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    /// <summary>Why sign-in failed, or why the user is back here.</summary>
    [ObservableProperty]
    public partial string? Message { get; set; }

    private bool CanSignIn() => ServerAddress.Trim().Length > 0 && UserName.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        Message = null;
        if (ServerUrl(ServerAddress) is not { } url)
        {
            Message = JellyfinException.InvalidUrl().Message;
            return;
        }
        try
        {
            var server = new JellyfinClient(url);
            var info = await server.PublicSystemInfoAsync();
            var result = await server.AuthenticateAsync(UserName.Trim(), Password);
            var session = new StoredSession
            {
                ServerUrl = url,
                ServerName = info.ServerName,
                UserId = result.User.Id,
                UserName = result.User.Name,
                AccessToken = result.AccessToken,
            };
            session.Save();
            signedIn(server.SignedIn(result.AccessToken, result.User.Id), session);
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException)
        {
            Message = error is HttpRequestException ? $"Couldn't reach the server: {error.Message}" : error.Message;
        }
    }

    /// <summary>
    /// What was typed as a server address, with http:// assumed when no
    /// scheme is given ("192.168.1.10:8096").
    /// </summary>
    internal static Uri? ServerUrl(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal)) trimmed = "http://" + trimmed;
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" ? url : null;
    }
}
