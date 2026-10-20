using System.Text.Json.Serialization;

namespace Aster.Mpv;

/// <summary>One entry of mpv's <c>track-list</c>.</summary>
public sealed record MpvTrack
{
    /// <summary>mpv's id for the track, counted within its type; what <c>aid</c> and <c>sid</c> take.</summary>
    [JsonPropertyName("id")] public int Id { get; init; }

    /// <summary>"video", "audio" or "sub".</summary>
    [JsonPropertyName("type")] public string Type { get; init; } = "";

    /// <summary>The stream's index in its container, which is how Jellyfin numbers a file's streams.</summary>
    [JsonPropertyName("ff-index")] public int? FfIndex { get; init; }

    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("lang")] public string? Language { get; init; }
    [JsonPropertyName("codec")] public string? Codec { get; init; }
    [JsonPropertyName("external")] public bool External { get; init; }
    [JsonPropertyName("external-filename")] public string? ExternalFilename { get; init; }
    [JsonPropertyName("selected")] public bool Selected { get; init; }
    [JsonPropertyName("default")] public bool Default { get; init; }
    [JsonPropertyName("forced")] public bool Forced { get; init; }
}
