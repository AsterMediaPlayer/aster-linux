using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Aster.Core.DiscMenu;
using Aster.Core.Jellyfin;
using Aster.Mpv;
using Avalonia.Threading;

namespace Aster.Desktop.Audio;

/// <summary>
/// A disc menu's music and button sounds (CLIENT_GUIDE §5.5): looping
/// music with volume fades, and the click, chime and beep presets, or the
/// sound files a menu ships with. A port of the Apple app's DiscMenuAudio.
///
/// The music plays in mpv. Button sounds go through SDL, so a click is heard
/// as the button's pressed; a menu's own sound files are decoded once, by
/// mpv, into samples for it.
/// </summary>
public sealed class DiscMenuAudio(JellyfinClient client, bool enabled) : IDisposable
{
    private readonly VolumeFades fades = new();
    private readonly SdlSound output = new();
    private MpvPlayer? music;
    private string? musicKey;
    private double musicVolume = 0.5;
    private bool suspended;

    private DiscSounds? sounds;
    private readonly Dictionary<DiscSound, Uri> soundFiles = [];

    /// <summary>Sound files decoded, by address; null while decoding, or when one couldn't be.</summary>
    private readonly Dictionary<Uri, float[]?> decoded = [];

    /// <summary>Tones made, by preset, kind and volume.</summary>
    private readonly Dictionary<(string?, DiscSound, double), float[]?> tones = [];

    private DateTime lastMove = DateTime.MinValue;

    // ---- Screen changes ----

    /// <summary>The screen's music and sounds (its own, else the document's).</summary>
    public void Apply(DiscAudio? audio, DiscMenuDocument document)
    {
        if (!enabled) return;
        var resolver = new DiscPictureResolver(client, "");
        sounds = audio?.Sounds;
        soundFiles.Clear();
        if (audio?.Sounds is { } spec)
        {
            foreach (var (kind, reference) in new[] { (DiscSound.Move, spec.Move), (DiscSound.Select, spec.Select), (DiscSound.Back, spec.Back) })
            {
                if (resolver.Audio(reference) is not { } file) continue;
                soundFiles[kind] = file;
                Decode(file);
            }
        }

        var url = audio?.Music switch
        {
            { Source: "file" } file => resolver.Audio(file.File),
            { Source: "themeSong" } when document.ThemeSongs is [var first, ..] => client.AudioStreamUrl(first),
            _ => null,
        };
        SetMusic(url, DiscMenuRules.Clamp(audio?.Music?.Volume, 0, 1, 0.5));
    }

    private void SetMusic(Uri? url, double volume)
    {
        var key = url?.AbsoluteUri;
        if (key == musicKey)
        {
            // Same track: just fade to the new volume.
            if (music is { } playing && musicVolume != volume)
            {
                musicVolume = volume;
                if (!suspended) fades.Fade(playing, VolumeFades.MpvVolume(volume), 0.4);
            }
            return;
        }
        if (music is { } old) fades.Fade(old, 0, 0.6, old.Dispose);
        musicKey = key;
        musicVolume = volume;
        music = null;
        if (url is null) return;
        try
        {
            music = MpvPlayer.Create(ThemePlayer.LoopingOptions(new()
            {
                ["video"] = "no",
                ["vo"] = "null",
                ["audio-display"] = "no",
                ["volume"] = "0",
                ["pause"] = suspended ? "yes" : "no",
            }));
            _ = music.CommandAsync("loadfile", url.AbsoluteUri).ContinueWith(_ => { });
            if (!suspended) fades.Fade(music, VolumeFades.MpvVolume(volume), 0.8);
        }
        catch (Exception error) when (error is MpvException or DllNotFoundException)
        {
            music = null;
        }
    }

    /// <summary>A title plays from the menu: the music fades and waits.</summary>
    public void Suspend()
    {
        suspended = true;
        if (music is { } playing) fades.Fade(playing, 0, 0.4, () => Try(() => playing.SetProperty("pause", true)));
    }

    public void Resume()
    {
        suspended = false;
        if (music is not { } paused) return;
        Try(() => paused.SetProperty("pause", false));
        fades.Fade(paused, VolumeFades.MpvVolume(musicVolume), 0.8);
    }

    // ---- Button sounds ----

    public void Play(DiscSound kind)
    {
        if (!enabled || sounds is not { } spec) return;
#if DEBUG
        if (Environment.GetEnvironmentVariable("ASTER_MUTE") is not null) return;
#endif
        if (kind == DiscSound.Move)
        {
            // A held arrow's moves don't each click.
            var now = DateTime.UtcNow;
            if (now - lastMove < TimeSpan.FromMilliseconds(45)) return;
            lastMove = now;
        }
        var volume = DiscMenuRules.Clamp(spec.Volume, 0, 1, 0.5);
        if (soundFiles.TryGetValue(kind, out var url) && decoded.GetValueOrDefault(url) is { } file)
        {
            output.Play(Scaled(file, volume));
            return;
        }
        if (!tones.TryGetValue((spec.Preset, kind, volume), out var tone))
        {
            tone = DiscTones.Render(spec.Preset, kind, volume);
            tones[(spec.Preset, kind, volume)] = tone;
        }
        if (tone is not null) output.Play(tone);
    }

    private static float[] Scaled(float[] samples, double volume)
    {
        var scaled = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++) scaled[i] = (float)(samples[i] * volume);
        return scaled;
    }

    /// <summary>
    /// Decodes a sound file to samples, once: mpv plays it into a file of
    /// raw 16-bit mono at SDL's rate, as fast as it can, and that's read
    /// once mpv has closed it.
    /// </summary>
    private void Decode(Uri url)
    {
        if (!decoded.TryAdd(url, null)) return;
        var path = Path.Combine(Path.GetTempPath(), $"aster-disc-sound-{Guid.NewGuid():N}.pcm");
        MpvPlayer player;
        try
        {
            player = MpvPlayer.Create(new Dictionary<string, string>
            {
                ["video"] = "no",
                ["ao"] = "pcm",
                ["ao-pcm-file"] = path,
                ["ao-pcm-waveheader"] = "no",
                ["audio-format"] = "s16",
                ["audio-channels"] = "mono",
                ["audio-samplerate"] = SdlSound.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["input-default-bindings"] = "no",
                ["terminal"] = "no",
                ["ytdl"] = "no",
            });
        }
        catch (Exception error) when (error is MpvException or DllNotFoundException)
        {
            return;
        }
        player.EndFile += end => Task.Run(() =>
        {
            // mpv finishes the file as it closes, which can't happen on its own event thread.
            player.Dispose();
            float[]? samples = null;
            try
            {
                if (end.Reason == MpvEndFileReason.Eof)
                {
                    var bytes = File.ReadAllBytes(path);
                    var pcm = MemoryMarshal.Cast<byte, short>(bytes.AsSpan(0, bytes.Length & ~1));
                    samples = new float[pcm.Length];
                    for (var i = 0; i < pcm.Length; i++) samples[i] = pcm[i] / 32768f;
                }
            }
            catch (IOException) { }
            finally
            {
                try { File.Delete(path); }
                catch (IOException) { }
            }
            Dispatcher.UIThread.Post(() => decoded[url] = samples);
        });
        _ = player.CommandAsync("loadfile", url.AbsoluteUri).ContinueWith(_ => { });
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is MpvException or ObjectDisposedException) { }
    }

    /// <summary>The menu closed: the music fades out, and the sounds stop.</summary>
    public void Dispose()
    {
        if (music is { } playing) fades.Fade(playing, 0, 0.4, playing.Dispose);
        music = null;
        musicKey = null;
        output.Dispose();
    }
}
