namespace Aster.Core.Jellyfin;

/// <summary>
/// What this device plays, sent with each playback request so the server
/// can decide between the original file and a transcode.
/// </summary>
public sealed record DeviceProfile
{
    public sealed record DirectPlayProfile
    {
        /// <summary>Null means any container, as Jellyfin reads an empty list.</summary>
        public string? Container { get; init; }

        public required string Type { get; init; }

        /// <summary>Null means any codec.</summary>
        public string? VideoCodec { get; init; }

        public string? AudioCodec { get; init; }
    }

    public sealed record TranscodingProfile
    {
        public required string Container { get; init; }
        public required string Type { get; init; }
        public string? VideoCodec { get; init; }
        public required string AudioCodec { get; init; }
        public string Context { get; init; } = "Streaming";
        public required string Protocol { get; init; }
        public string MaxAudioChannels { get; init; } = "6";
        public int MinSegments { get; init; } = 2;
        public bool BreakOnNonKeyFrames { get; init; } = true;
        public bool CopyTimestamps { get; init; }

        /// <summary>
        /// Off: subtitle renditions in the server's HLS playlists play 10 s
        /// late (Jellyfin issue 16647); the player is given the tracks itself.
        /// </summary>
        public bool EnableSubtitlesInManifest { get; init; }
    }

    public sealed record SubtitleProfile(string Format, string Method);

    public sealed record ProfileCondition(string Condition, string Property, string Value, bool IsRequired = false);

    public sealed record CodecProfile
    {
        public required string Type { get; init; }
        public string? Codec { get; init; }
        public IReadOnlyList<ProfileCondition> Conditions { get; init; } = [];
    }

    public string Name { get; init; } = "Aster Windows";
    public int MaxStreamingBitrate { get; init; }
    public int MaxStaticBitrate { get; init; }
    public int MusicStreamingTranscodingBitrate { get; init; } = 320_000;
    public IReadOnlyList<DirectPlayProfile> DirectPlayProfiles { get; init; } = [];
    public IReadOnlyList<TranscodingProfile> TranscodingProfiles { get; init; } = [];
    public IReadOnlyList<SubtitleProfile> SubtitleProfiles { get; init; } = [];
    public IReadOnlyList<CodecProfile> CodecProfiles { get; init; } = [];

    /// <summary>
    /// mpv plays practically any file, so every container and codec direct
    /// plays, leaving the server to transcode only for bitrate. This takes
    /// the place of the Apple TV profile, which lists what VideoToolbox and
    /// AVPlayer can decode.
    /// </summary>
    public static DeviceProfile Desktop(int maxBitrate)
    {
        // Lossless needs a few Mbps of headroom, so it leads only when the
        // quality setting leaves room; otherwise AAC keeps the video sharp.
        var losslessFirst = maxBitrate >= 20_000_000;
        var fmp4Audio = losslessFirst ? "flac,eac3,ac3,opus,aac" : "aac,eac3,ac3,opus,flac";
        return new DeviceProfile
        {
            MaxStreamingBitrate = maxBitrate,
            MaxStaticBitrate = maxBitrate,
            DirectPlayProfiles =
            [
                new() { Type = "Video" },
                new() { Type = "Audio" },
            ],
            TranscodingProfiles =
            [
                new() { Container = "mp4", Type = "Video", VideoCodec = "hevc,h264", AudioCodec = fmp4Audio, Protocol = "hls", MaxAudioChannels = "8" },
                // MPEG-TS can't carry FLAC.
                new() { Container = "ts", Type = "Video", VideoCodec = "h264", AudioCodec = "aac,ac3,eac3", Protocol = "hls" },
                new() { Container = "aac", Type = "Audio", AudioCodec = "aac", Protocol = "hls" },
            ],
            // mpv draws every kind of subtitle itself, picture-based ones
            // (PGS, VobSub, DVB) included, so none are ever burned in. Text
            // tracks in a transcode come as separate files.
            SubtitleProfiles =
            [
                .. new[] { "vtt", "subrip", "srt", "ass", "ssa", "mov_text", "ttml", "webvtt" }.Select(f => new SubtitleProfile(f, "External")),
                .. new[] { "vtt", "subrip", "srt", "ass", "ssa", "mov_text", "ttml", "webvtt", "pgssub", "pgs", "dvdsub", "vobsub", "dvbsub", "sub", "idx", "smi", "cc_dec" }.Select(f => new SubtitleProfile(f, "Embed")),
            ],
        };
    }

    /// <summary>
    /// Only a fresh 8-bit H.264 + AAC encode. Used when a direct or copied
    /// stream has already failed for a title.
    /// </summary>
    public static DeviceProfile DesktopMostCompatible(int maxBitrate) => Desktop(maxBitrate) with
    {
        Name = "Aster Windows (most compatible)",
        DirectPlayProfiles = [],
        TranscodingProfiles =
        [
            new() { Container = "mp4", Type = "Video", VideoCodec = "h264", AudioCodec = "aac", Protocol = "hls" },
            new() { Container = "ts", Type = "Video", VideoCodec = "h264", AudioCodec = "aac", Protocol = "hls" },
            new() { Container = "aac", Type = "Audio", AudioCodec = "aac", Protocol = "hls" },
        ],
        CodecProfiles =
        [
            new()
            {
                Type = "Video",
                Codec = "h264",
                Conditions =
                [
                    new("LessThanEqual", "VideoLevel", "52"),
                    new("LessThanEqual", "VideoBitDepth", "8"),
                    new("NotEquals", "IsAnamorphic", "true"),
                ],
            },
        ],
    };
}
