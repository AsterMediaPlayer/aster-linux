using System;
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
    }
}
