using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Aster.Core.Audio;

/// <summary>A Sonos speaker on the network: its id (RINCON_…), its room, and where to reach it.</summary>
public sealed record SonosSpeaker(string Id, string Room, IPAddress Address);

/// <summary>What a track is, for a speaker's display and the Sonos app's.</summary>
public sealed record SonosTrack(string Title, string? Artist, string? Album, Uri? Art, bool IsBook = false);

/// <summary>A room as Sonos groups them: its speaker (a home theatre's soundbar, not its surrounds) and the group it's in.</summary>
public sealed record SonosRoom(string Id, string Name, IPAddress Address, string Coordinator)
{
    public SonosSpeaker Speaker => new(Id, Name, Address);
}

/// <summary>
/// A Sonos home-theatre speaker's volume, for passthrough: a Dolby or DTS
/// stream sent untouched can't be turned down by the player, only by the
/// speaker decoding it. Sonos speakers take UPnP volume commands on the
/// network (as the Sonos app does). The one to use is the one on its TV
/// input (<c>x-sonos-htastream:</c> its own id), found by asking every
/// speaker that answers a discovery search; with two on TV input at once,
/// the one used before, else the listener says which. Searched for both ways at once: SSDP, whose
/// answers come straight back to the asker (dropped by a firewall, as
/// ufw's on a desktop), and mDNS, whose answers are multicast (let through
/// wherever devices are found at all).
/// </summary>
public static partial class Sonos
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>The speakers on their own TV inputs (a soundbar each), by room.</summary>
    public static async Task<IReadOnlyList<SonosSpeaker>> OnTvInputAsync(CancellationToken ct = default)
    {
        var onTv = new List<SonosSpeaker>();
        foreach (var speaker in await DiscoverAsync(TimeSpan.FromSeconds(2), ct))
        {
            if (IsOwnTvInput(await CurrentUriAsync(speaker, ct), speaker.Id)) onTv.Add(speaker);
        }
        return [.. onTv.OrderBy(s => s.Room, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// The one to use of those on TV input: the only one, or the one used
    /// before. Null with several and none used before: two TVs are on, and
    /// nothing a speaker says tells which this computer is plugged into.
    /// </summary>
    public static SonosSpeaker? Choose(IReadOnlyList<SonosSpeaker> onTv, string? preferred) =>
        onTv.FirstOrDefault(s => s.Id == preferred) ?? (onTv.Count == 1 ? onTv[0] : null);

    /// <summary>Turns <paramref name="speaker"/> up or down by <paramref name="by"/> (of 100): its volume after, or null when it didn't answer.</summary>
    public static async Task<int?> ChangeVolumeAsync(SonosSpeaker speaker, int by, CancellationToken ct = default)
    {
        var reply = await SoapAsync(speaker, "RenderingControl", "SetRelativeVolume",
            $"<InstanceID>0</InstanceID><Channel>Master</Channel><Adjustment>{by.ToString(CultureInfo.InvariantCulture)}</Adjustment>", ct);
        return reply is not null && Tag(reply, "NewVolume") is { } level && int.TryParse(level, out var volume) ? volume : null;
    }

    // ---- Rooms, groups and playing (Sonos's UPnP, as the Sonos app uses) ----

    /// <summary>Every room, from the first speaker that answers (each knows the whole system), by name.</summary>
    public static async Task<IReadOnlyList<SonosRoom>> RoomsAsync(CancellationToken ct = default)
    {
        foreach (var speaker in await DiscoverAsync(TimeSpan.FromSeconds(2), ct))
        {
            var reply = await SoapAsync(speaker, "ZoneGroupTopology", "GetZoneGroupState", "", ct, "/ZoneGroupTopology/Control");
            if (reply is not null && Tag(reply, "ZoneGroupState") is { } state) return Rooms(WebUtility.HtmlDecode(state));
        }
        return [];
    }

    /// <summary>
    /// The rooms in a ZoneGroupState: each visible ZoneGroupMember (a bonded
    /// surround or sub is a Satellite, and hidden members are Invisible),
    /// with its group's coordinator.
    /// </summary>
    internal static IReadOnlyList<SonosRoom> Rooms(string zoneGroupState)
    {
        var rooms = new List<SonosRoom>();
        foreach (Match group in ZoneGroup().Matches(zoneGroupState))
        {
            var coordinator = group.Groups[1].Value;
            foreach (Match member in ZoneGroupMember().Matches(group.Groups[2].Value))
            {
                var attributes = new Dictionary<string, string>();
                foreach (Match attribute in Attribute().Matches(member.Groups[1].Value)) attributes[attribute.Groups[1].Value] = WebUtility.HtmlDecode(attribute.Groups[2].Value);
                if (attributes.GetValueOrDefault("Invisible") == "1") continue;
                if (attributes.GetValueOrDefault("UUID") is not { } id || attributes.GetValueOrDefault("ZoneName") is not { } name) continue;
                if (!Uri.TryCreate(attributes.GetValueOrDefault("Location"), UriKind.Absolute, out var location) || !IPAddress.TryParse(location.Host, out var address)) continue;
                rooms.Add(new SonosRoom(id, name, address, coordinator));
            }
        }
        return [.. rooms.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Whether <paramref name="speaker"/> is playing its own TV input (a soundbar the TV is sounding through).</summary>
    public static async Task<bool> IsOnTvInputAsync(SonosSpeaker speaker, CancellationToken ct = default) =>
        IsOwnTvInput(await CurrentUriAsync(speaker, ct), speaker.Id);

    /// <summary>Hands <paramref name="uri"/> to <paramref name="coordinator"/>'s group, with what it is for the speakers' own display.</summary>
    public static async Task<bool> PlayUriAsync(SonosSpeaker coordinator, Uri uri, SonosTrack track, CancellationToken ct = default) =>
        await SoapAsync(coordinator, "AVTransport", "SetAVTransportURI",
            $"<InstanceID>0</InstanceID><CurrentURI>{Escape(uri.AbsoluteUri)}</CurrentURI><CurrentURIMetaData>{Escape(Didl(uri, track))}</CurrentURIMetaData>", ct) is not null;

    public static async Task<bool> PlayAsync(SonosSpeaker coordinator, CancellationToken ct = default) =>
        await SoapAsync(coordinator, "AVTransport", "Play", "<InstanceID>0</InstanceID><Speed>1</Speed>", ct) is not null;

    public static async Task<bool> PauseAsync(SonosSpeaker coordinator, CancellationToken ct = default) =>
        await SoapAsync(coordinator, "AVTransport", "Pause", "<InstanceID>0</InstanceID>", ct) is not null;

    public static async Task<bool> StopAsync(SonosSpeaker coordinator, CancellationToken ct = default) =>
        await SoapAsync(coordinator, "AVTransport", "Stop", "<InstanceID>0</InstanceID>", ct) is not null;

    public static async Task<bool> SeekAsync(SonosSpeaker coordinator, double seconds, CancellationToken ct = default) =>
        await SoapAsync(coordinator, "AVTransport", "Seek", $"<InstanceID>0</InstanceID><Unit>REL_TIME</Unit><Target>{Time(seconds)}</Target>", ct) is not null;

    /// <summary>Where the group's track has got to and its length, in seconds (the length 0 when it doesn't say).</summary>
    public static async Task<(double Position, double Duration)?> PositionAsync(SonosSpeaker coordinator, CancellationToken ct = default)
    {
        var reply = await SoapAsync(coordinator, "AVTransport", "GetPositionInfo", "<InstanceID>0</InstanceID>", ct);
        if (reply is null || Seconds(Tag(reply, "RelTime")) is not { } position) return null;
        return (position, Seconds(Tag(reply, "TrackDuration")) ?? 0);
    }

    /// <summary>PLAYING, PAUSED_PLAYBACK, STOPPED, TRANSITIONING or NO_MEDIA_PRESENT; null when it didn't answer.</summary>
    public static async Task<string?> TransportStateAsync(SonosSpeaker coordinator, CancellationToken ct = default)
    {
        var reply = await SoapAsync(coordinator, "AVTransport", "GetTransportInfo", "<InstanceID>0</InstanceID>", ct);
        return reply is null ? null : Tag(reply, "CurrentTransportState");
    }

    /// <summary>Back to its own TV input and playing it, as a soundbar was before it was taken for music.</summary>
    public static async Task<bool> BackToTvAsync(SonosSpeaker speaker, CancellationToken ct = default)
    {
        var tv = $"x-sonos-htastream:{speaker.Id}:spdif";
        var set = await SoapAsync(speaker, "AVTransport", "SetAVTransportURI",
            $"<InstanceID>0</InstanceID><CurrentURI>{Escape(tv)}</CurrentURI><CurrentURIMetaData></CurrentURIMetaData>", ct);
        return set is not null && await PlayAsync(speaker, ct);
    }

    /// <summary><paramref name="member"/> joins <paramref name="coordinatorId"/>'s group, to play in sync with it.</summary>
    public static async Task<bool> JoinAsync(SonosSpeaker member, string coordinatorId, CancellationToken ct = default) =>
        await SoapAsync(member, "AVTransport", "SetAVTransportURI",
            $"<InstanceID>0</InstanceID><CurrentURI>x-rincon:{Escape(coordinatorId)}</CurrentURI><CurrentURIMetaData></CurrentURIMetaData>", ct) is not null;

    /// <summary><paramref name="member"/> leaves its group, a room on its own again.</summary>
    public static async Task<bool> LeaveGroupAsync(SonosSpeaker member, CancellationToken ct = default) =>
        await SoapAsync(member, "AVTransport", "BecomeCoordinatorOfStandaloneGroup", "<InstanceID>0</InstanceID>", ct) is not null;

    /// <summary>The whole group's volume turned by <paramref name="by"/>, as the Sonos app's group slider: the group's level after.</summary>
    public static async Task<int?> ChangeGroupVolumeAsync(SonosSpeaker coordinator, int by, CancellationToken ct = default)
    {
        var reply = await SoapAsync(coordinator, "GroupRenderingControl", "SetRelativeGroupVolume",
            $"<InstanceID>0</InstanceID><Adjustment>{by.ToString(CultureInfo.InvariantCulture)}</Adjustment>", ct);
        return reply is not null && Tag(reply, "NewVolume") is { } level && int.TryParse(level, out var volume) ? volume : null;
    }

    /// <summary>A room's own volume (0 to 100), or null when it didn't answer.</summary>
    public static async Task<int?> VolumeAsync(SonosSpeaker speaker, CancellationToken ct = default)
    {
        var reply = await SoapAsync(speaker, "RenderingControl", "GetVolume", "<InstanceID>0</InstanceID><Channel>Master</Channel>", ct);
        return reply is not null && Tag(reply, "CurrentVolume") is { } level && int.TryParse(level, out var volume) ? volume : null;
    }

    public static async Task<bool> SetVolumeAsync(SonosSpeaker speaker, int level, CancellationToken ct = default) =>
        await SoapAsync(speaker, "RenderingControl", "SetVolume",
            $"<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredVolume>{Math.Clamp(level, 0, 100).ToString(CultureInfo.InvariantCulture)}</DesiredVolume>", ct) is not null;

    /// <summary>
    /// The extension a Sonos speaker knows a file by, for a container as the
    /// server names it (sometimes a list: "mov,mp4,m4a,3gp,3g2,mj2"): a
    /// speaker tells what it's handed from its address, and refuses one it
    /// can't place (UPnP error 714). Null for a format Sonos doesn't play.
    /// </summary>
    public static string? Extension(string? container)
    {
        if (container is null) return null;
        var names = container.ToLowerInvariant().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (names.Contains("flac")) return "flac";
        if (names.Contains("mp3")) return "mp3";
        if (names.Intersect(["m4a", "m4b", "mp4", "aac", "alac"]).Any()) return "m4a";
        if (names.Intersect(["ogg", "oga"]).Any()) return "ogg";
        if (names.Contains("wav")) return "wav";
        if (names.Intersect(["aiff", "aif"]).Any()) return "aiff";
        return null;
    }

    /// <summary>h:mm:ss, as Sonos's times are written.</summary>
    internal static string Time(double seconds)
    {
        var whole = (long)Math.Max(0, Math.Round(seconds));
        return $"{whole / 3600}:{whole / 60 % 60:00}:{whole % 60:00}";
    }

    /// <summary>Seconds from Sonos's h:mm:ss, or null for NOT_IMPLEMENTED and the like.</summary>
    internal static double? Seconds(string? time)
    {
        if (time is null) return null;
        var parts = time.Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return null;
        return h * 3600 + m * 60 + s;
    }

    /// <summary>The DIDL-Lite a speaker shows (and the Sonos app) for a track it's handed.</summary>
    internal static string Didl(Uri uri, SonosTrack track)
    {
        var item = new StringBuilder();
        item.Append("<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" ");
        item.Append("xmlns:r=\"urn:schemas-rinconnetworks-com:metadata-1-0/\" xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">");
        item.Append("<item id=\"-1\" parentID=\"-1\" restricted=\"true\">");
        item.Append($"<res protocolInfo=\"http-get:*:*:*\">{Escape(uri.AbsoluteUri)}</res>");
        item.Append($"<dc:title>{Escape(track.Title)}</dc:title>");
        item.Append($"<upnp:class>{(track.IsBook ? "object.item.audioItem.audioBook" : "object.item.audioItem.musicTrack")}</upnp:class>");
        if (track.Artist is { } artist) item.Append($"<dc:creator>{Escape(artist)}</dc:creator>");
        if (track.Album is { } album) item.Append($"<upnp:album>{Escape(album)}</upnp:album>");
        if (track.Art is { } art) item.Append($"<upnp:albumArtURI>{Escape(art.AbsoluteUri)}</upnp:albumArtURI>");
        item.Append("</item></DIDL-Lite>");
        return item.ToString();
    }

    private static string Escape(string text) => System.Security.SecurityElement.Escape(text) ?? "";

    [GeneratedRegex(@"<ZoneGroup Coordinator=""([^""]+)""[^>]*>(.*?)</ZoneGroup>", RegexOptions.Singleline)]
    private static partial Regex ZoneGroup();

    [GeneratedRegex(@"<ZoneGroupMember ([^>]*?)/?>")]
    private static partial Regex ZoneGroupMember();

    [GeneratedRegex(@"(\w+)=""([^""]*)""")]
    private static partial Regex Attribute();

    /// <summary>
    /// Whether sound is coming in on <paramref name="speaker"/>'s TV input:
    /// false when it says no audio is (HTAudioIn 22, or 0 with nothing
    /// connected), true for anything it's decoding, null when it didn't
    /// answer. 59 is Dolby Atmos in Dolby Digital Plus, 61 in TrueHD.
    /// </summary>
    public static async Task<bool?> HearsTvAsync(SonosSpeaker speaker, CancellationToken ct = default)
    {
        var reply = await SoapAsync(speaker, "DeviceProperties", "GetZoneInfo", "", ct, "/DeviceProperties/Control");
        return reply is not null && Tag(reply, "HTAudioIn") is { } code && int.TryParse(code, out var input) ? HearsInput(input) : null;
    }

    internal static bool HearsInput(int htAudioIn) => htAudioIn is not (0 or 21 or 22);

    /// <summary>The speakers that answer a search for Sonos players (SSDP or mDNS) within <paramref name="wait"/>.</summary>
    public static async Task<IReadOnlyList<SonosSpeaker>> DiscoverAsync(TimeSpan wait, CancellationToken ct = default)
    {
        var searches = await Task.WhenAll(SsdpAsync(wait, ct), MdnsAsync(wait, ct));
        var speakers = new List<SonosSpeaker>();
        foreach (var location in searches.SelectMany(found => found).Distinct())
        {
            try
            {
                if (Describe(await Http.GetStringAsync(location, ct), location) is { } speaker && speakers.All(s => s.Id != speaker.Id)) speakers.Add(speaker);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { }
        }
        return speakers;
    }

    /// <summary>An SSDP search for zone players: the description each answer points to.</summary>
    private static async Task<IReadOnlyList<Uri>> SsdpAsync(TimeSpan wait, CancellationToken ct)
    {
        var locations = new HashSet<Uri>();
        using (var socket = new UdpClient(AddressFamily.InterNetwork))
        {
            var search = Encoding.ASCII.GetBytes(
                "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: urn:schemas-upnp-org:device:ZonePlayer:1\r\n\r\n");
            try { await socket.SendAsync(search, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900), ct); }
            catch (SocketException) { return []; }
            using var until = CancellationTokenSource.CreateLinkedTokenSource(ct);
            until.CancelAfter(wait);
            try
            {
                while (true)
                {
                    var answer = await socket.ReceiveAsync(until.Token);
                    if (Location(Encoding.ASCII.GetString(answer.Buffer)) is { } location) locations.Add(location);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (SocketException) { }
        }
        return [.. locations];
    }

    /// <summary>
    /// An mDNS question for <c>_sonos._tcp.local</c>, asked and heard on the
    /// mDNS group itself (beside avahi or Bonjour, which share the port): each
    /// answer comes from a speaker, whose description is at its port 1400.
    /// </summary>
    private static async Task<IReadOnlyList<Uri>> MdnsAsync(TimeSpan wait, CancellationToken ct)
    {
        var group = IPAddress.Parse("224.0.0.251");
        var locations = new HashSet<Uri>();
        try
        {
            using var socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
            socket.JoinMulticastGroup(group);
            await socket.SendAsync(MdnsQuestion, new IPEndPoint(group, 5353), ct);
            using var until = CancellationTokenSource.CreateLinkedTokenSource(ct);
            until.CancelAfter(wait);
            try
            {
                while (true)
                {
                    var answer = await socket.ReceiveAsync(until.Token);
                    if (IsSonosAnswer(answer.Buffer)) locations.Add(new Uri($"http://{answer.RemoteEndPoint.Address}:1400/xml/device_description.xml"));
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        catch (SocketException) { }
        return [.. locations];
    }

    /// <summary>A standard (multicast-answered) question for the PTR records of _sonos._tcp.local.</summary>
    private static readonly byte[] MdnsQuestion =
    [
        0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0,
        6, (byte)'_', (byte)'s', (byte)'o', (byte)'n', (byte)'o', (byte)'s',
        4, (byte)'_', (byte)'t', (byte)'c', (byte)'p',
        5, (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l', 0,
        0, 12, 0, 1,
    ];

    /// <summary>An mDNS response (not our own question, or another's) naming _sonos.</summary>
    internal static bool IsSonosAnswer(byte[] packet) =>
        packet.Length > 12 && (packet[2] & 0x80) != 0 && packet.AsSpan().IndexOf("_sonos"u8) >= 0;

    /// <summary>The LOCATION header of an SSDP answer: where the speaker describes itself.</summary>
    internal static Uri? Location(string answer)
    {
        var match = LocationHeader().Match(answer);
        return match.Success && Uri.TryCreate(match.Groups[1].Value.Trim(), UriKind.Absolute, out var uri) ? uri : null;
    }

    /// <summary>A speaker from its device description (its UDN and room).</summary>
    internal static SonosSpeaker? Describe(string description, Uri location)
    {
        if (Tag(description, "UDN") is not { } udn || Tag(description, "roomName") is not { } room || !IPAddress.TryParse(location.Host, out var address)) return null;
        return new SonosSpeaker(udn.StartsWith("uuid:", StringComparison.Ordinal) ? udn[5..] : udn, WebUtility.HtmlDecode(room), address);
    }

    /// <summary>
    /// Playing its own TV input: <c>x-sonos-htastream:RINCON_…:spdif</c> with
    /// its id. (A surround or sub bonded to it isn't, nor a speaker grouped
    /// with it, which would name the soundbar's id.)
    /// </summary>
    internal static bool IsOwnTvInput(string? uri, string id) =>
        uri is not null && uri.StartsWith($"x-sonos-htastream:{id}:", StringComparison.Ordinal);

    private static async Task<string?> CurrentUriAsync(SonosSpeaker speaker, CancellationToken ct)
    {
        var reply = await SoapAsync(speaker, "AVTransport", "GetMediaInfo", "<InstanceID>0</InstanceID>", ct);
        return reply is null ? null : Tag(reply, "CurrentURI");
    }

    private static async Task<string?> SoapAsync(SonosSpeaker speaker, string service, string action, string arguments, CancellationToken ct, string? path = null)
    {
        var body = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">"
            + $"<s:Body><u:{action} xmlns:u=\"urn:schemas-upnp-org:service:{service}:1\">{arguments}</u:{action}></s:Body></s:Envelope>";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{speaker.Address}:1400{path ?? $"/MediaRenderer/{service}/Control"}")
        {
            Content = new StringContent(body, Encoding.UTF8, "text/xml"),
        };
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"urn:schemas-upnp-org:service:{service}:1#{action}\"");
        try
        {
            using var response = await Http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { return null; }
    }

    private static string? Tag(string xml, string name)
    {
        var match = Regex.Match(xml, $"<{name}>([^<]*)</{name}>");
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^LOCATION:\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex LocationHeader();
}
