# Aster for Windows

An Avalonia port of Aster Media Player, in progress. The Apple app in the
folders above is the reference: `Aster.Core` ports its `Jellyfin/` layer
file by file, so keep the two in step when either changes.

| Project | What it is |
| --- | --- |
| `src/Aster.Core` | Jellyfin client, models, device profile, resume points, session storage (Windows Credential Manager), subtitle and trickplay helpers, JellyDisc models |
| `src/Aster.Mpv` | Bindings for libmpv: the player, its events, and OpenGL rendering into the UI |
| `src/Aster.Desktop` | The Avalonia app: sign-in, a first Home screen, and the player |
| `tests/Aster.Core.Tests` | xUnit tests for the core (against a stub server) and for libmpv (with a clip made by ffmpeg, when it's installed) |

## Build and run

Needs the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`). The first
build downloads libmpv (about 120 MB) into `native/` with
`tools/fetch-libmpv.ps1`, pinned to one build and checked against its hash.

```
dotnet run --project src/Aster.Desktop
dotnet test
```

### Trying it against a server (debug builds)

These leave the saved session alone:

- `ASTER_SERVER`, `ASTER_TOKEN` (an API key or access token) and
  `ASTER_USER_ID` sign in for that run.
- `--play <itemId>` opens a title straight away, from its resume point;
  `--start <seconds>` and `--max-bitrate <bps>` change where it starts and
  cap its quality (which makes the server convert it).
- `ASTER_LOG=<file>` writes the app's and mpv's messages to a file:
  which stream was chosen, the track mapping, the decoder, dropped frames.

Playing a title this way reports progress and saves resume points like
any other playback.

## The player

mpv plays the file from the server as it is, so every audio and subtitle
track is in hand (ASS with its styling and fonts, PGS, VobSub) and
switches instantly. Over the quality setting, or with a lower quality
chosen, the server converts it to HLS instead, with the chosen text
subtitle loaded beside it and a picture one burned in. A stream that fails
steps down: the server's own choice, then a plain H.264 encode, then an
error. Hardware decoding is on (`d3d11va`).

Skip Intro / Skip Credits come from the server's media segments, Up Next
counts down at the end of an episode, progress goes to the server every 10
seconds, and resume points follow the Apple app's rules.

Keys: Space or K pause, ← → (J L) 10 seconds, ↑ ↓ volume, M mute, A next
audio track, S or C next subtitle track, Enter takes the skip on offer, F or
a double-click full screen, Esc leaves full screen and then the player.

Not yet: Live TV, Channels, OP/ED song cards, trickplay previews,
chapters, downloads, and HDR passthrough (HDR is tone-mapped to SDR, since
the video is drawn through OpenGL so the controls can sit on top of it).

## Differences from the Apple app

- **Device profile:** playback uses mpv (libmpv), which plays nearly any
  file, so `DeviceProfile.Desktop` direct-plays every container
  and codec and embeds every subtitle format, instead of listing what
  VideoToolbox and AVPlayer decode.
- **Session storage:** Windows Credential Manager, under `Aster/session`,
  in place of the Keychain.
- **Image cache:** a small disk cache in `%LOCALAPPDATA%\Aster\ImageCache`
  in place of URLCache.
- **Settings on the server** use the same display-preferences record as the
  Apple app (`asterfin`), so they follow a user between the two.
