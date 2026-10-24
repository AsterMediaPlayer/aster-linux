using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>A group to pick: its name, whether the channel's in it, and picking it.</summary>
public sealed record GroupChoice(string Title, bool IsCurrent, IAsyncRelayCommand Pick);

/// <summary>
/// Puts a Live TV channel in a group: none, one in use, or a new one. For an
/// administrator; the Aster Channels plugin keeps it, so every device's guide
/// has the same groups. A port of the Apple app's LiveChannelGroupSheet.
/// </summary>
public sealed partial class LiveChannelGroupViewModel : ViewModelBase
{
    private readonly INavigator navigator;
    private readonly LiveTVViewModel live;
    private readonly string channelId;

    public LiveChannelGroupViewModel(INavigator navigator, LiveTVViewModel live, BaseItem channel)
    {
        this.navigator = navigator;
        this.live = live;
        channelId = channel.Id;
        Title = string.Join(" ", new[] { channel.ChannelNumber, channel.DisplayTitle }.OfType<string>());
        var current = live.GroupOf(channel.Id);
        Choices = new[] { ("None", (string?)null) }.Concat(live.GroupNames.Select(g => (g, (string?)g)))
            .Select(c => new GroupChoice(c.Item1, c.Item2 == current, new AsyncRelayCommand(() => PickAsync(c.Item2))))
            .ToList();
    }

    public string Title { get; }
    public IReadOnlyList<GroupChoice> Choices { get; }

    [ObservableProperty] public partial string NewGroup { get; set; } = "";
    [ObservableProperty] public partial bool IsSaving { get; private set; }
    [ObservableProperty] public partial string? Message { get; private set; }

    [RelayCommand]
    private Task AddAsync() => NewGroup.Trim() is { Length: > 0 } name ? PickAsync(name) : Task.CompletedTask;

    private async Task PickAsync(string? group)
    {
        IsSaving = true;
        Message = null;
        try
        {
            await live.SetGroupAsync(channelId, group);
            navigator.Back();
        }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException)
        {
            Message = $"Couldn't save the group. {error.Message}";
            IsSaving = false;
        }
    }
}
