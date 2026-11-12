using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aster.Core.Diagnostics;
using Aster.Core.Profiles;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>A person in the household as a tile: their picture (or initial on their colour) and name.</summary>
public sealed partial class ProfileTileViewModel(HouseholdProfile profile, Action<HouseholdProfile> pick) : ObservableObject
{
    public HouseholdProfile Profile { get; } = profile;
    public string Name => Profile.Name;
    public string Initial => Profile.Initial;
    public Uri? ImageUrl => Profile.ImageUrl;
    public IBrush Background => ProfileColors.Brush(Profile.ColorHex);
    public bool ShowsLock => Profile.RequiresPin;
    public bool IsCurrent => Profile.IsCurrent;
    public string Description => Profile.AccessibleDescription;

    /// <summary>Being switched to: a spinner over the picture.</summary>
    [ObservableProperty] public partial bool IsSwitching { get; set; }

    /// <summary>Another is being switched to: faded back.</summary>
    [ObservableProperty] public partial bool IsDimmed { get; set; }

    [RelayCommand] private void Pick() => pick(Profile);
}

public static class ProfileColors
{
    /// <summary><c>#RRGGBB</c> (or <c>RRGGBB</c>); a grey for anything else.</summary>
    public static IBrush Brush(string hex)
    {
        var digits = hex.Trim().TrimStart('#');
        return digits.Length == 6 && uint.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out var value)
            ? new SolidColorBrush(Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value))
            : new SolidColorBrush(Color.FromRgb(0x4D, 0x4D, 0x4D));
    }
}

/// <summary>
/// "Who's watching?": the household's people as tiles. Picking one switches
/// at once, or asks for their PIN first; the app then starts afresh as
/// theirs. Shown as the app opens (when the household asks for it) and
/// whenever someone asks to switch. The main account can add someone from
/// the end of the row. The Apple app's ProfilePickerView and its model.
/// </summary>
public sealed partial class ProfilePickerViewModel : ViewModelBase
{
    private readonly HouseholdModel household;
    private readonly IServerAccounts accounts;
    private readonly Action dismiss;
    private readonly Action<ViewModelBase> showPage;

    public ProfilePickerViewModel(ProfilePickerReason reason, HouseholdModel household, IServerAccounts accounts, string userName, string serverName,
        Action dismiss, Action<ViewModelBase> showPage)
    {
        Reason = reason;
        this.household = household;
        this.accounts = accounts;
        this.dismiss = dismiss;
        this.showPage = showPage;
        UserName = userName;
        ServerName = serverName;
        _ = LoadAsync();
    }

    public ProfilePickerReason Reason { get; }
    public string ServerName { get; }
    public string UserName { get; }

    /// <summary>The footer's way out: carrying on as whoever's signed in as the app opens, or not switching after all.</summary>
    public string FooterTitle => Reason == ProfilePickerReason.Launch ? $"Continue as {UserName}" : "Cancel";

    /// <summary>Locked, there's no way out but a PIN (or another profile).</summary>
    public bool HasFooter => Reason != ProfilePickerReason.Locked;

    public bool IsLocked => Reason == ProfilePickerReason.Locked;

    /// <summary>Its own copy: the tiles stay as they are while a switch goes through.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTiles))]
    public partial IReadOnlyList<ProfileTileViewModel> Tiles { get; private set; } = [];

    public bool HasTiles => Tiles.Count > 0;

    /// <summary>The main account, signed in, can add someone: an Add Profile tile at the end of the row.</summary>
    public bool CanAdd => household.CanCreateProfiles && Reason != ProfilePickerReason.Locked;

    [ObservableProperty] public partial bool IsLoading { get; private set; }
    [ObservableProperty] public partial string? LoadError { get; private set; }
    [ObservableProperty] public partial string? ErrorMessage { get; private set; }

    /// <summary>The profile whose PIN is being asked; null while picking.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnteringPin), nameof(IsPicking), nameof(PinPrompt))]
    public partial HouseholdProfile? PinFor { get; private set; }

    public bool IsEnteringPin => PinFor is not null;
    public bool IsPicking => PinFor is null;
    public string PinPrompt => PinFor is { } profile
        ? IsLocked && profile.IsCurrent ? $"Enter {profile.Name}'s PIN to carry on" : $"Enter {profile.Name}'s PIN"
        : "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitPinCommand))]
    public partial string Pin { get; set; } = "";

    /// <summary>The profile being switched to (from a tile, or once its PIN was typed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSwitching))]
    [NotifyCanExecuteChangedFor(nameof(SubmitPinCommand), nameof(FooterCommand), nameof(AddCommand))]
    public partial HouseholdProfile? Switching { get; private set; }

    public bool IsSwitching => Switching is not null;

    /// <summary>Counts the tries that failed, so the view can clear and refocus the PIN each time.</summary>
    public event Action? PinRefused;

    /// <summary>The tiles as last seen, at once; then the server's fresh list (a PIN can stop being asked on the home network, say).</summary>
    private async Task LoadAsync()
    {
        await household.RestoreCachedAsync();
        if (household.Profiles.Count > 0) ShowTiles(household.Profiles);
        IsLoading = Tiles.Count == 0;
        await household.RefreshAsync(force: true);
        IsLoading = false;
        if (household.Provider is not null)
        {
            ShowTiles(household.Profiles);
            LoadError = null;
        }
        else if (Tiles.Count == 0)
        {
            LoadError = household.LastError ?? new ProfileSwitchException(ProfileSwitchKind.Unavailable).Message;
        }
        OnPropertyChanged(nameof(CanAdd));
        // Opened to ask who's watching, and there's no one else to pick: straight in.
        if (Reason == ProfilePickerReason.Launch && household.Checked && (household.Provider is null || Tiles.Count <= 1)) dismiss();
        // Locked: the profile's own PIN at once; one that asks none now (the home network's skip) goes straight back.
        if (Reason == ProfilePickerReason.Locked && household.Provider is not null)
        {
            if (household.Current is { RequiresPin: true } current) PinFor = current;
            else if (household.Current is not null) dismiss();
        }
    }

    /// <summary>The household as the server has it now (someone was just added).</summary>
    public async Task ReloadAsync()
    {
        await household.RefreshAsync(force: true);
        if (household.Provider is not null) ShowTiles(household.Profiles);
        OnPropertyChanged(nameof(CanAdd));
    }

    private void ShowTiles(IReadOnlyList<HouseholdProfile> profiles) =>
        Tiles = profiles.Select(p => new ProfileTileViewModel(p, picked => _ = PickAsync(picked))).ToList();

    private async Task PickAsync(HouseholdProfile profile)
    {
        if (IsSwitching) return;
        ErrorMessage = null;
        switch (ProfileSwitchPlan.Make(profile, accounts.ReusableSession(profile.Id)))
        {
            // Locked, even the one signed in needs their PIN to carry on.
            case ProfileSwitchPlan.AlreadyCurrent when IsLocked:
                Pin = "";
                PinFor = profile;
                break;
            case ProfileSwitchPlan.AlreadyCurrent:
                dismiss();
                break;
            case ProfileSwitchPlan.NeedsPin:
                Pin = "";
                PinFor = profile;
                break;
            case ProfileSwitchPlan.ReuseStored(var stored):
                MarkSwitching(profile);
                // A beat, so the tile is seen to be chosen before the screen changes.
                await Task.Delay(250);
                accounts.SwitchProfile(stored);
                break;
            default:
                await PerformAsync(profile, null);
                break;
        }
    }

    private bool CanSubmitPin() => PinFor is not null && Pin.Length > 0 && !IsSwitching;

    [RelayCommand(CanExecute = nameof(CanSubmitPin))]
    private Task SubmitPinAsync() => PinFor is { } profile ? PerformAsync(profile, Pin) : Task.CompletedTask;

    [RelayCommand]
    private void CancelPin()
    {
        if (PinFor is null || IsSwitching) return;
        PinFor = null;
        Pin = "";
        ErrorMessage = null;
    }

    private bool NotSwitching() => !IsSwitching;

    /// <summary>Continue as whoever's signed in, or Cancel.</summary>
    [RelayCommand(CanExecute = nameof(NotSwitching))]
    private void Footer() => dismiss();

    /// <summary>Back (Esc, a controller's B): leaves the PIN first, then the picker (not when locked: the tiles stay, to pick someone else).</summary>
    public void GoBack()
    {
        if (IsSwitching) return;
        if (IsEnteringPin) CancelPin();
        else if (!IsLocked) dismiss();
    }

    /// <summary>Adding someone: the form over the picker; the row then shows them.</summary>
    [RelayCommand(CanExecute = nameof(NotSwitching))]
    private void Add()
    {
        showPage(new ProfileCreateViewModel(household, () =>
        {
            showPage(this);
            _ = ReloadAsync();
        }));
    }

    private async Task PerformAsync(HouseholdProfile profile, string? pin)
    {
        if (household.Provider is not { } provider)
        {
            ErrorMessage = new ProfileSwitchException(ProfileSwitchKind.Unavailable).Message;
            return;
        }
        MarkSwitching(profile);
        ErrorMessage = null;
        try
        {
            // Locked: the one signed in carries on once the server says the PIN's right.
            if (IsLocked && profile.IsCurrent)
            {
                await provider.VerifyPinAsync(pin ?? "", profile);
                DebugLog.Note("Profiles", "Unlocked");
                dismiss();
                return;
            }
            var fresh = await provider.SwitchSessionAsync(profile, pin);
            accounts.SwitchProfile(fresh);
        }
        catch (Exception error) when (error is ProfileSwitchException or Aster.Core.Jellyfin.JellyfinException or System.Net.Http.HttpRequestException or TimeoutException)
        {
            DebugLog.Note("Profiles", $"Switch to {profile.Name} failed: {error.Message}");
            MarkSwitching(null);
            ErrorMessage = error.Message;
            // A wrong PIN (or too many tries): the PIN again. Anything else: back to the tiles.
            if (error is ProfileSwitchException { Kind: ProfileSwitchKind.WrongPin or ProfileSwitchKind.RateLimited or ProfileSwitchKind.PinNeeded })
            {
                PinFor = profile;
                Pin = "";
                PinRefused?.Invoke();
            }
            else PinFor = null;
        }
    }

    private void MarkSwitching(HouseholdProfile? profile)
    {
        Switching = profile;
        foreach (var tile in Tiles)
        {
            tile.IsSwitching = profile is not null && tile.Profile.Id == profile.Id;
            tile.IsDimmed = profile is not null && tile.Profile.Id != profile.Id;
        }
    }
}
