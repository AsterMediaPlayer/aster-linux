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
