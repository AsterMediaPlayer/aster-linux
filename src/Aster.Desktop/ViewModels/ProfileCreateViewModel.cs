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

/// <summary>A picture, a colour, a rating or a library to pick: ringed (or ticked) when chosen.</summary>
public sealed partial class ProfileChoice(string label, Action? pick = null) : ObservableObject
{
    public string Label { get; } = label;

    /// <summary>A picture's address; null for the letter on the colour.</summary>
    public Uri? ImageUrl { get; init; }

    /// <summary>A library's tick: picking it turns it over, apart from the others.</summary>
    public bool Toggles { get; init; }

    /// <summary>A colour's swatch, or the letter tile's (which follows the colour picked).</summary>
    [ObservableProperty] public partial IBrush? Swatch { get; set; }

    [ObservableProperty] public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Pick()
    {
        if (Toggles) IsSelected = !IsSelected;
        pick?.Invoke();
    }
}

/// <summary>
/// Adding someone to the household: a name, a picture (or a letter on a
/// colour), a PIN if they want one, how far up the ratings they may watch,
/// and which libraries they see. The main account's own PIN is asked when it
/// has one. Made on the server; the picker then shows them. The Apple app's
/// ProfileCreateView.
/// </summary>
public sealed partial class ProfileCreateViewModel : ViewModelBase
{
    private readonly HouseholdModel household;
    private readonly Action done;
    private ProfileCreationOptions? options;

    public ProfileCreateViewModel(HouseholdModel household, Action done)
    {
        this.household = household;
        this.done = done;
        Colors = ProfileDraft.Colors.Select(hex => new ProfileChoice("Colour", () => ColorHex = hex) { Swatch = ProfileColors.Brush(hex), IsSelected = hex == ColorHex }).ToList();
        _ = LoadAsync();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initial))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial string Name { get; set; } = "";

    public string Initial => new ProfileDraft { Name = Name }.Initial;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPin), nameof(PinNote))]
    public partial string Pin { get; set; } = "";

    [ObservableProperty] public partial string ConfirmPin { get; set; } = "";
    [ObservableProperty] public partial bool BypassPinOnLocalNetwork { get; set; }
    [ObservableProperty] public partial string MasterPin { get; set; } = "";

    public bool HasPin => Pin.Length > 0;

    public string PinNote => HasPin
        ? "Switching to this profile asks for the PIN; without it, anyone on the household's devices can open it."
        : "4 to 8 digits. With a PIN, switching to this profile asks for it.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewBrush))]
    public partial string ColorHex { get; set; } = Aster.Core.Profiles.Bonfire.DefaultAvatarColor;

    public IBrush PreviewBrush => ProfileColors.Brush(ColorHex);

    /// <summary>One of the offered pictures, or null for the initial on the colour.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewImage))]
    public partial string? AvatarId { get; set; }

    public Uri? PreviewImage => AvatarId is { } id ? options?.Avatars.FirstOrDefault(a => a.Id == id)?.Url : null;

    public IReadOnlyList<ProfileChoice> Colors { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPictures))]
    public partial IReadOnlyList<ProfileChoice> Pictures { get; private set; } = [];

    public bool HasPictures => Pictures.Count > 0;

    /// <summary>Everything up to this rating; null for no limit beyond the account's.</summary>
    private int? ratingScore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRatings))]
    public partial IReadOnlyList<ProfileChoice> Ratings { get; private set; } = [];

    public bool HasRatings => Ratings.Count > 1;

    [ObservableProperty] public partial string RatingNote { get; private set; } = "";

    /// <summary>Every library the account has, now and later; off, only those ticked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LibraryNote))]
    public partial bool AllLibraries { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLibraries))]
    public partial IReadOnlyList<ProfileChoice> Libraries { get; private set; } = [];

    public bool HasLibraries => Libraries.Count > 0;

    public string LibraryNote => AllLibraries ? "Everything the main account can see, including libraries added later." : "Only the libraries ticked.";

    [ObservableProperty] public partial bool AsksMasterPin { get; private set; }
    [ObservableProperty] public partial string? Remaining { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial bool IsLoaded { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand), nameof(CancelCommand))]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty] public partial string? ErrorMessage { get; private set; }

    partial void OnColorHexChanged(string value)
    {
        for (var i = 0; i < Colors.Count; i++) Colors[i].IsSelected = ProfileDraft.Colors[i] == value;
        // The letter tile takes the colour too.
        if (Pictures.FirstOrDefault() is { ImageUrl: null } letter) letter.Swatch = PreviewBrush;
    }

    private async Task LoadAsync()
    {
        options = await household.CreationOptionsAsync();
        Pictures = options.Avatars.Count == 0 ? []
            : [new ProfileChoice("Letter", PickLetter) { Swatch = PreviewBrush, IsSelected = true },
               .. options.Avatars.Select(a => new ProfileChoice(a.Name, () => PickPicture(a.Id)) { ImageUrl = a.Url })];
        Ratings = options.Ratings.Count == 0 ? []
            : [new ProfileChoice("No Limit", () => PickRating(null)) { IsSelected = true },
               .. options.Ratings.Select(r => new ProfileChoice(r.Label, () => PickRating(r.Score)))];
        RatingNote = $"Up to this rating{(options.RatingCountry is { } country ? $" ({country})" : "")}, and never past what the main account may watch.";
        Libraries = options.Libraries.Select(l => new ProfileChoice(l.Name) { Toggles = true, IsSelected = true }).ToList();
        AsksMasterPin = options.AsksMasterPin;
        Remaining = options.Remaining is { } left ? left == 1 ? "Room for one more profile." : $"Room for {left} more profiles." : null;
        IsLoaded = true;
    }

    private void PickLetter() => Select(Pictures, 0, () => AvatarId = null);

    private void PickPicture(string id) => Select(Pictures, Pictures.ToList().FindIndex(p => p.ImageUrl == options?.Avatars.FirstOrDefault(a => a.Id == id)?.Url), () => AvatarId = id);

    private void PickRating(int? score)
    {
        ratingScore = score;
        var index = score is null ? 0 : 1 + options!.Ratings.ToList().FindIndex(r => r.Score == score);
        Select(Ratings, index, () => { });
    }

    private static void Select(IReadOnlyList<ProfileChoice> choices, int index, Action apply)
    {
        for (var i = 0; i < choices.Count; i++) choices[i].IsSelected = i == index;
        apply();
    }

    private bool CanCreate() => IsLoaded && !IsSaving && Name.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        var draft = new ProfileDraft
        {
            Name = Name, Pin = Pin, AvatarId = AvatarId, ColorHex = ColorHex, RatingScore = ratingScore,
            LibraryIds = AllLibraries ? null : options!.Libraries.Where((_, i) => Libraries[i].IsSelected).Select(l => l.Id).ToHashSet(),
            MasterPin = MasterPin, BypassPinOnLocalNetwork = BypassPinOnLocalNetwork,
        };
        if (draft.ValidationMessage(ConfirmPin) is { } message)
        {
            ErrorMessage = message;
            return;
        }
        if (AsksMasterPin && MasterPin.Length == 0)
        {
            ErrorMessage = "Enter the main account's PIN.";
            return;
        }
        IsSaving = true;
        ErrorMessage = null;
        try
        {
            await household.CreateAsync(draft);
            done();
        }
        catch (Exception error) when (error is ProfileSwitchException or Aster.Core.Jellyfin.JellyfinException or System.Net.Http.HttpRequestException or TimeoutException)
        {
            DebugLog.Note("Profiles", $"Making a profile failed: {error.Message}");
            ErrorMessage = error.Message;
        }
        finally { IsSaving = false; }
    }

    private bool NotSaving() => !IsSaving;

    [RelayCommand(CanExecute = nameof(NotSaving))]
    private void Cancel() => done();
}
