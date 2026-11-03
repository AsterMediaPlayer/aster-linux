using System;
using System.Threading.Tasks;
using Aster.Core.Audio;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// A row of the speaker picker: this computer, or a Sonos room with its own
/// volume. A room playing its TV asks first: chosen once it says so, chosen
/// again it switches.
/// </summary>
public sealed partial class SpeakerChoice : ObservableObject
{
    private readonly Action<SpeakerChoice> toggled;
    private int? sentVolume;

    /// <summary>The level being shown is the speaker's own: not to be sent back to it.</summary>
    private bool fromSpeaker;

    /// <summary>When the slider was last moved here, so a reading from the speaker doesn't pull it back mid-drag.</summary>
    private DateTime movedHere = DateTime.MinValue;

    /// <summary>
    /// The room's volume as the speaker says it is now (turned from the
    /// controller, the Sonos app, its own buttons), unless the slider's just
    /// been moved here.
    /// </summary>
    public void ShowVolume(int level)
    {
        if (DateTime.UtcNow - movedHere < TimeSpan.FromSeconds(1.5) || Math.Abs(Volume - level) < 0.5) return;
        fromSpeaker = true;
        sentVolume = level;
        Volume = level;
        fromSpeaker = false;
    }

    public SpeakerChoice(string name, SonosRoom? room, bool selected, bool onTv, int? volume, Action<SpeakerChoice> toggled)
    {
        Name = name;
        Room = room;
        IsSelected = selected;
        IsOnTv = onTv;
        HasVolume = room is not null && volume is not null;
        Volume = volume ?? 0;
        sentVolume = volume;
        this.toggled = toggled;
    }

    public string Name { get; }

    /// <summary>The room, or null for this computer.</summary>
    public SonosRoom? Room { get; }

    public bool IsOnTv { get; }

    public bool HasVolume { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Mark), nameof(ShowsVolume))]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    public partial bool IsAsking { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeLabel))]
    public partial double Volume { get; set; }

    public string Mark => IsSelected ? "✓" : "";

    public bool ShowsVolume => HasVolume && IsSelected;

    public string Label => Name;

    /// <summary>Under the name: a room playing its TV, and what choosing it again does.</summary>
    public string? Detail => IsAsking ? "Playing the TV: choose again to switch it to this" : IsOnTv ? "Playing the TV" : null;

    public string VolumeLabel => $"{Math.Round(Volume):0}";

    public string VolumeName => $"{Name} volume";

    [RelayCommand] private void Toggle() => toggled(this);

    // The room's own volume, sent once the slider has settled.
    partial void OnVolumeChanged(double value)
    {
        if (Room is not { } room || fromSpeaker) return;
        movedHere = DateTime.UtcNow;
        var level = (int)Math.Round(value);
        if (level == sentVolume) return;
        sentVolume = level;
        _ = SendAsync(room, level);
    }

    private int sending;

    /// <summary>Sent once the slider's settled for a quarter second: only the latest level, so quick moves can't land out of order.</summary>
    private async Task SendAsync(SonosRoom room, int level)
    {
        var mine = ++sending;
        await Task.Delay(250);
        if (mine != sending) return;
        await Sonos.SetVolumeAsync(room.Speaker, level);
    }
}
