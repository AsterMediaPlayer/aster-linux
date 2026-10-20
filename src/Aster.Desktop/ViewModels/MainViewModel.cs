using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aster.Desktop.ViewModels;

/// <summary>Which screen the window shows: sign-in, or the signed-in app.</summary>
public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial ViewModelBase? CurrentPage { get; set; }

    public MainViewModel()
    {
        if (StoredSession.Load() is { } session)
        {
            var client = new JellyfinClient(session.ServerUrl, session.AccessToken, session.UserId);
            ShowHome(client, session);
        }
        else
        {
            ShowSignIn();
        }
    }

    private void ShowSignIn(string? message = null) =>
        CurrentPage = new SignInViewModel(ShowHome) { Message = message };

    private void ShowHome(JellyfinClient client, StoredSession session) =>
        CurrentPage = new HomeViewModel(client, session, SignOutAsync);

    private async Task SignOutAsync(JellyfinClient client, bool expired)
    {
        if (!expired) await client.LogoutAsync();
        StoredSession.Clear();
        ShowSignIn(expired ? "Your session has ended. Please sign in again." : null);
    }
}
