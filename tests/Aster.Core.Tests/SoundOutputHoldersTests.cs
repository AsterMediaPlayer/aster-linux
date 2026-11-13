using Aster.Core.Audio;

namespace Aster.Core.Tests;

/// <summary>Who PipeWire says is on the output passthrough needs, from <c>pw-dump</c>'s objects (cut down to the fields read).</summary>
public sealed class SoundOutputHoldersTests
{
    private const int Self = 4242;

    // The HDMI output (passthrough-capable), the laptop speakers (PCM only), a meter on
    // the HDMI monitor (PulseAudio Volume Control), a browser on the speakers, and this
    // app's own stream on HDMI, whose name comes from its client.
    private const string Dump = """
        [
          { "id": 63, "type": "PipeWire:Interface:Node", "info": { "props": { "media.class": "Audio/Sink", "node.name": "hdmi", "iec958.codecs": "[\"PCM\",\"DTS\",\"AC3\",\"EAC3\",\"TrueHD\"]" } } },
          { "id": 64, "type": "PipeWire:Interface:Node", "info": { "props": { "media.class": "Audio/Sink", "node.name": "speakers", "iec958.codecs": "[\"PCM\"]" } } },
          { "id": 79, "type": "PipeWire:Interface:Node", "info": { "props": { "media.class": "Stream/Input/Audio", "application.name": "PulseAudio Volume Control", "application.process.id": "189010" } } },
          { "id": 80, "type": "PipeWire:Interface:Node", "info": { "props": { "media.class": "Stream/Output/Audio", "application.name": "Chromium", "application.process.id": "2000" } } },
          { "id": 77, "type": "PipeWire:Interface:Client", "info": { "props": { "application.name": "Aster.Desktop", "application.process.id": "4242" } } },
          { "id": 81, "type": "PipeWire:Interface:Node", "info": { "props": { "media.class": "Stream/Output/Audio", "node.name": "mpv", "client.id": 77 } } },
          { "id": 90, "type": "PipeWire:Interface:Link", "info": { "output-node-id": 63, "input-node-id": 79 } },
          { "id": 91, "type": "PipeWire:Interface:Link", "info": { "output-node-id": 63, "input-node-id": 79 } },
          { "id": 92, "type": "PipeWire:Interface:Link", "info": { "output-node-id": 80, "input-node-id": 64 } },
          { "id": 93, "type": "PipeWire:Interface:Link", "info": { "output-node-id": 81, "input-node-id": 63 } },
          { "id": 94, "type": "PipeWire:Interface:Metadata", "info": null }
        ]
        """;

    [Fact]
    public void NamesWhoElseIsOnThePassthroughOutput_Once() =>
        Assert.Equal(["PulseAudio Volume Control"], SoundOutputHolders.Parse(Dump, Self));

    [Fact]
    public void ThisAppItselfIsLeftOut_ByItsClientsProcess() =>
        Assert.Contains("Aster.Desktop", SoundOutputHolders.Parse(Dump, self: 1));

    [Fact]
    public void NoOneOnIt() =>
        Assert.Empty(SoundOutputHolders.Parse("""[ { "id": 63, "type": "PipeWire:Interface:Node", "info": { "props": { "media.class": "Audio/Sink", "iec958.codecs": "[\"PCM\",\"AC3\"]" } } } ]""", Self));
}
