using System;
using System.Linq;
using System.Net;
using Aster.Core.Audio;

namespace Aster.Core.Tests;

/// <summary>
/// Finding the Sonos on the TV's input, for the volume in passthrough: an
/// SSDP answer's location, a device description (trimmed from a real Arc's),
/// and which speaker is playing its own TV input.
/// </summary>
public class SonosTests
{
    [Fact]
    public void AnAnswersLocationIsRead()
    {
        const string answer = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age = 1800\r\nLOCATION: http://192.168.1.64:1400/xml/device_description.xml\r\nST: urn:schemas-upnp-org:device:ZonePlayer:1\r\n\r\n";
        Assert.Equal(new Uri("http://192.168.1.64:1400/xml/device_description.xml"), Sonos.Location(answer));
        Assert.Null(Sonos.Location("HTTP/1.1 200 OK\r\n\r\n"));
    }

    [Fact]
    public void ADescriptionGivesItsIdAndRoom()
    {
        const string description = "<root><device><roomName>Shop Soundbar</roomName><modelName>Sonos Arc</modelName><UDN>uuid:RINCON_38420BEEBC9B01400</UDN></device></root>";
        var speaker = Sonos.Describe(description, new Uri("http://192.168.1.64:1400/xml/device_description.xml"));
        Assert.Equal(new SonosSpeaker("RINCON_38420BEEBC9B01400", "Shop Soundbar", IPAddress.Parse("192.168.1.64")), speaker);
    }

    [Fact]
    public void OnlyTheSoundbarOnItsOwnTvInputCounts()
    {
        Assert.True(Sonos.IsOwnTvInput("x-sonos-htastream:RINCON_38420BEEBC9B01400:spdif", "RINCON_38420BEEBC9B01400"));
        // Grouped with it, or a surround bonded to it: the soundbar's id, not its own.
        Assert.False(Sonos.IsOwnTvInput("x-sonos-htastream:RINCON_38420BEEBC9B01400:spdif", "RINCON_7828CA18912601400"));
        Assert.False(Sonos.IsOwnTvInput("http://192.168.1.240:5005/tts/clip.mp3", "RINCON_74CA6060C99C01400"));
        Assert.False(Sonos.IsOwnTvInput(null, "RINCON_74CA6060C99C01400"));
    }

    [Fact]
    public void RoomsAreTheVisibleMembersWithTheirGroups()
    {
        // Trimmed from this house's: a soundbar with a surround (a Satellite, not a room), a hidden member, and a group of two.
        const string state = """
            <ZoneGroupState><ZoneGroups>
            <ZoneGroup Coordinator="RINCON_A" ID="RINCON_A:1"><ZoneGroupMember UUID="RINCON_A" Location="http://192.168.1.51:1400/xml/device_description.xml" ZoneName="Living Room" HTSatChanMapSet="x"><Satellite UUID="RINCON_S" Location="http://192.168.1.58:1400/xml/device_description.xml" ZoneName="Living Room" Invisible="1"/></ZoneGroupMember></ZoneGroup>
            <ZoneGroup Coordinator="RINCON_K" ID="RINCON_K:2"><ZoneGroupMember UUID="RINCON_K" Location="http://192.168.1.153:1400/xml/device_description.xml" ZoneName="Kitchen"/><ZoneGroupMember UUID="RINCON_D" Location="http://192.168.1.73:1400/xml/device_description.xml" ZoneName="Dining &amp; Bar"/></ZoneGroup>
            <ZoneGroup Coordinator="RINCON_H" ID="RINCON_H:3"><ZoneGroupMember UUID="RINCON_H" Location="http://192.168.1.9:1400/xml/device_description.xml" ZoneName="Hidden" Invisible="1"/></ZoneGroup>
            </ZoneGroups></ZoneGroupState>
            """;
        var rooms = Sonos.Rooms(state);
        Assert.Equal(["Dining & Bar", "Kitchen", "Living Room"], rooms.Select(r => r.Name));
        Assert.Equal("RINCON_K", rooms[0].Coordinator);
        Assert.Equal(IPAddress.Parse("192.168.1.51"), rooms[2].Address);
    }

    [Fact]
    public void TimesAreSonosHoursMinutesSeconds()
    {
        Assert.Equal("0:00:00", Sonos.Time(0));
        Assert.Equal("1:02:05", Sonos.Time(3725.4));
        Assert.Equal(3725, Sonos.Seconds("1:02:05"));
        Assert.Null(Sonos.Seconds("NOT_IMPLEMENTED"));
    }

    [Fact]
    public void MetadataIsEscaped()
    {
        var didl = Sonos.Didl(new Uri("https://jf.example/Audio/1/stream?a=1&b=2"), new SonosTrack("Rock & Roll <Live>", "AC/DC", null, null));
        Assert.Contains("<dc:title>Rock &amp; Roll &lt;Live&gt;</dc:title>", didl);
        Assert.Contains("a=1&amp;b=2", didl);
        Assert.Contains("musicTrack", didl);
    }

    [Fact]
    public void AFileIsHandedOverByAnExtensionSonosKnows()
    {
        Assert.Equal("flac", Sonos.Extension("flac"));
        Assert.Equal("m4a", Sonos.Extension("mov,mp4,m4a,3gp,3g2,mj2"));
        Assert.Equal("m4a", Sonos.Extension("m4b"));
        Assert.Equal("mp3", Sonos.Extension("MP3"));
        Assert.Null(Sonos.Extension("opus"));
        Assert.Null(Sonos.Extension(null));
    }

    [Fact]
    public void NoAudioOnTheTvInputIsTold()
    {
        // As an Arc reported them: 22 with a Dolby stream sent that never reached it, 59 once it did.
        Assert.False(Sonos.HearsInput(22));
        Assert.False(Sonos.HearsInput(0));
        Assert.True(Sonos.HearsInput(59));
        Assert.True(Sonos.HearsInput(61));
    }

    [Fact]
    public void ATieGoesToTheSpeakerUsedBeforeOrToNone()
    {
        var shop = new SonosSpeaker("RINCON_A", "Shop Soundbar", IPAddress.Parse("192.168.1.64"));
        var bedroom = new SonosSpeaker("RINCON_B", "Bedroom", IPAddress.Parse("192.168.1.52"));
        Assert.Equal(bedroom, Sonos.Choose([bedroom, shop], "RINCON_B"));
        // Two TVs on and neither used before: not a guess (it once picked the bedroom's).
        Assert.Null(Sonos.Choose([bedroom, shop], null));
        Assert.Null(Sonos.Choose([bedroom, shop], "RINCON_GONE"));
        Assert.Equal(shop, Sonos.Choose([shop], null));
        Assert.Null(Sonos.Choose([], "RINCON_B"));
        // The one used before off its TV: another on its TV doesn't take its place unasked.
        Assert.Null(Sonos.Choose([bedroom], "RINCON_A"));
    }
}
