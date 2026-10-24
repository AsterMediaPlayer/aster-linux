using System;
using SDL;
using static SDL.SDL3;

namespace Aster.Desktop.Audio;

/// <summary>
/// Short sounds played the moment they're asked for (a disc menu's button
/// sounds), through SDL3's audio, which the app has along for controllers.
/// Each sound cuts off the one before, as a remote's clicks do. Opened the
/// first time something plays; without SDL or an audio device, it's silent.
/// </summary>
public sealed unsafe class SdlSound : IDisposable
{
    public const int SampleRate = Aster.Core.DiscMenu.DiscTones.SampleRate;

    private SDL_AudioStream* stream;
    private bool tried;

    /// <summary>Plays mono 32-bit samples at <see cref="SampleRate"/>.</summary>
    public void Play(float[] samples)
    {
        if (samples.Length == 0 || !Open()) return;
        SDL_ClearAudioStream(stream);
        fixed (float* data = samples) SDL_PutAudioStreamData(stream, (IntPtr)data, samples.Length * sizeof(float));
    }

    private bool Open()
    {
        if (stream is not null) return true;
        if (tried) return false;
        tried = true;
        try
        {
            // As the controllers' SDL: Ctrl+C stays the app's.
            SDL_SetHint(SDL_HINT_NO_SIGNAL_HANDLERS, "1"u8);
            if (!SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO))
            {
                System.Diagnostics.Trace.WriteLine($"Sounds: SDL's audio didn't start: {SDL_GetError()}");
                return false;
            }
            var spec = new SDL_AudioSpec { format = SDL_AUDIO_F32, channels = 1, freq = SampleRate };
            stream = SDL_OpenAudioDeviceStream(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, null, IntPtr.Zero);
            if (stream is null)
            {
                System.Diagnostics.Trace.WriteLine($"Sounds: no audio device: {SDL_GetError()}");
                SDL_QuitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO);
                return false;
            }
            // A device stream opens paused.
            SDL_ResumeAudioStreamDevice(stream);
            return true;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (stream is null) return;
        SDL_DestroyAudioStream(stream);
        stream = null;
        SDL_QuitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO);
    }
}
