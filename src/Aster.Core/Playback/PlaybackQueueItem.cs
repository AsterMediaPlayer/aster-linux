namespace Aster.Core.Playback;

/// <summary>
/// One thing to play: an item, from an offset, optionally with chosen
/// tracks. A subtitle index of -1 means none, even if the server would
/// default one. The Apple app's entry also carries clip and theme fields;
/// those come with the features that use them.
/// </summary>
public sealed record PlaybackQueueItem
{
    public required string ItemId { get; init; }
    public long StartTicks { get; init; }
    public int? AudioStreamIndex { get; init; }
    public int? SubtitleStreamIndex { get; init; }

    /// <summary>The file version the track indexes belong to, once one has played.</summary>
    public string? MediaSourceId { get; init; }

    /// <summary>How far this entry has stepped back after failing to play.</summary>
    public StreamFallback Fallback { get; init; } = StreamFallback.None;

    /// <summary>
    /// A bitrate cap for this playback only, chosen in the player when the
    /// original file was too much for the connection; null uses the setting.
    /// </summary>
    public int? MaxBitrate { get; init; }

    /// <summary>
    /// Played as part of this Channel's schedule: no markers or Up Next, its
    /// resume point and watched state left alone, and what the channel shows
    /// next playing when it ends.
    /// </summary>
    public string? ChannelId { get; init; }

    /// <summary>A break clip cut short to land on the mark: it ends here, as if it had played out.</summary>
    public long? ChannelCutTicks { get; init; }
}

/// <summary>
/// The streams tried in turn when a title won't play, each asking the server
/// for something safer. Kept per queue entry, so a track change keeps it and
/// the next episode starts afresh.
/// </summary>
public enum StreamFallback
{
    /// <summary>Direct play when the profile allows it, else what the server picks.</summary>
    None,

    /// <summary>Direct play and direct stream off; the server may still copy streams.</summary>
    ServerTranscode,

    /// <summary>A fresh 8-bit H.264 + AAC encode, copying nothing from the file.</summary>
    MostCompatible,
}
