using Aster.Core.Jellyfin;
using Aster.Core.Playback;

namespace Aster.Core.Tests;

public class TrackRulesTests
{
    private static readonly IReadOnlyList<MediaStream> Subtitles =
    [
        new() { Type = "Subtitle", Index = 3, Language = "eng", IsForced = true },
        new() { Type = "Subtitle", Index = 4, Language = "jpn" },
        new() { Type = "Subtitle", Index = 5, Language = "eng" },
    ];

    private static readonly MediaSourceInfo Source = new() { Id = "s", DefaultSubtitleStreamIndex = 4 };

    private static int Resolve(SubtitleMode mode, string? language = "en", int? chosen = null) =>
        TrackRules.ResolveSubtitle(new PlaybackQueueItem { ItemId = "i", SubtitleStreamIndex = chosen }, Source, Subtitles,
            new PlaybackPreferences { SubtitleMode = mode, SubtitleLanguage = language });

    [Fact]
    public void AnExplicitChoiceWins() => Assert.Equal(-1, Resolve(SubtitleMode.Always, chosen: -1));

    [Fact]
    public void SubtitleModesPickAsTheServerDoes()
    {
        Assert.Equal(-1, Resolve(SubtitleMode.None));
        Assert.Equal(5, Resolve(SubtitleMode.Always));
        Assert.Equal(3, Resolve(SubtitleMode.OnlyForced));
        Assert.Equal(4, Resolve(SubtitleMode.Default));
        Assert.Equal(4, Resolve(SubtitleMode.Always, language: "fre"));
    }

    [Fact]
    public void PreferredAudioMatchesThreeAndTwoLetterCodes()
    {
        IReadOnlyList<MediaStream> streams =
        [
            new() { Type = "Audio", Index = 1, Language = "jpn" },
            new() { Type = "Audio", Index = 2, Language = "eng" },
            new() { Type = "Audio", Index = 3, Language = "eng", IsDefault = true },
        ];
        Assert.Equal(3, TrackRules.PreferredAudioTrack(streams, "en"));
        Assert.Equal(1, TrackRules.PreferredAudioTrack(streams, "ja"));
        Assert.Null(TrackRules.PreferredAudioTrack(streams, null));
        Assert.Null(TrackRules.PreferredAudioTrack(streams, "deu"));
    }

    [Theory]
    [InlineData("ger", "de", true)]
    [InlineData("deu", "ger", true)]
    [InlineData("chi", "zh", true)]
    [InlineData("eng", "jpn", false)]
    [InlineData(null, "eng", false)]
    [InlineData("und", "und", false)]
    public void SameLanguage(string? a, string? b, bool same) => Assert.Equal(same, LanguageTag.SameLanguage(a, b));

    [Fact]
    public void PreferencesComeFromTheUsersServerSettings()
    {
        var preferences = TrackRules.Preferences(new UserConfiguration { AudioLanguagePreference = "jpn", SubtitleMode = "OnlyForced" });
        Assert.Equal("jpn", preferences.AudioLanguage);
        Assert.Equal(SubtitleMode.OnlyForced, preferences.SubtitleMode);
        Assert.Equal(SubtitleMode.Default, TrackRules.Preferences(null).SubtitleMode);
    }

    [Fact]
    public void StallWatchOffersAfterALongStallOrRepeatedOnes()
    {
        var t = DateTimeOffset.UnixEpoch;
        var watch = new StallWatch();
        Assert.False(watch.Note(true, t));
        Assert.False(watch.Note(true, t.AddSeconds(4)));
        Assert.True(watch.Note(true, t.AddSeconds(5)));

        watch.Declined(t.AddSeconds(5));
        Assert.False(watch.Note(true, t.AddSeconds(60)));

        var repeats = new StallWatch();
        foreach (var at in new[] { 0, 10, 20 })
        {
            repeats.Note(false, t.AddSeconds(at - 1));
            repeats.Note(true, t.AddSeconds(at));
        }
        Assert.True(repeats.Note(true, t.AddSeconds(20)));
    }
}
