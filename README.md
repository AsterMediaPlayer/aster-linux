# Aster for Windows

An Avalonia port of Aster Media Player, in progress. The Apple app in the
folders above is the reference: `Aster.Core` ports its `Jellyfin/` layer
file by file, so keep the two in step when either changes.

| Project | What it is |
| --- | --- |
| `src/Aster.Core` | Jellyfin client, models, device profile, resume points, session storage (Windows Credential Manager), subtitle and trickplay helpers, JellyDisc models |
| `src/Aster.Desktop` | The Avalonia app (sign-in and a first Home screen so far) |
| `tests/Aster.Core.Tests` | xUnit tests for the core, run against a stub server |

## Build and run

Needs the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`).

```
dotnet run --project src/Aster.Desktop
dotnet test
```

## Differences from the Apple app

- **Device profile:** playback is planned to use mpv (libmpv), which plays
  nearly any file, so `DeviceProfile.Desktop` direct-plays every container
  and codec and embeds every subtitle format, instead of listing what
  VideoToolbox and AVPlayer decode.
- **Session storage:** Windows Credential Manager, under `Aster/session`,
  in place of the Keychain.
- **Image cache:** a small disk cache in `%LOCALAPPDATA%\Aster\ImageCache`
  in place of URLCache.
- **Settings on the server** use the same display-preferences record as the
  Apple app (`asterfin`), so they follow a user between the two.
