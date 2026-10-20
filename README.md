# Aster for Windows

An Avalonia port of Aster Media Player, in progress. The Apple app in the
folders above is the reference: `Aster.Core` ports its `Jellyfin/` layer
file by file, so keep the two in step when either changes.

| Project | What it is |
| --- | --- |
| `src/Aster.Core` | Jellyfin client, models, device profile, resume points, session storage (Windows Credential Manager), subtitle and trickplay helpers, JellyDisc models |
| `src/Aster.Mpv` | Bindings for libmpv: the player, its events, and OpenGL rendering into the UI |
| `src/Aster.Desktop` | The Avalonia app: sign-in, Home, Movies, TV Shows, Anime, Library, title and person pages, and the player |
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
- `--open <itemId>` opens a title's (or library's, or person's) page.
- `--tab <movies|shows|anime|library>` starts on a tab; `--sort <Name|DateAdded|ReleaseDate|Rating>` sorts it.
- Signing out of such a session only ends it; the saved sign-in is left alone.
- `--play <itemId>` plays a title straight away, from its resume point;
  `--start <seconds>` and `--max-bitrate <bps>` change where it starts and
  cap its quality (which makes the server convert it).
- `ASTER_LOG=<file>` writes the app's and mpv's messages to a file:
  which stream was chosen, the track mapping, the decoder, dropped frames.

Playing a title this way reports progress and saves resume points like
any other playback.

## Browsing

Home is the Apple app's: a featured banner as tall as the window (up to 8
titles from Continue Watching, Up Next and the newest movies and shows, its
Play starting the title with its page behind), then Continue Watching, Up
Next, the newest movies, shows and anime, and a shelf per other library.
Anime libraries are told apart as the Apple app does, by sampling titles for
Shoko/AniDB ids.

A title's page has its backdrop, logo, details and actions (Play, Resume
or Play Next, From Beginning, favourite, watched, Go to Series), then for a
show or season tabs of episodes by season, extras, related and details;
for an episode the rest of its season, the next one in view; for a movie
its extras, related titles, cast and information. Right-clicking a card
gives the Apple app's long-press menu. After playing, a page shows where
viewing got to, following on to a later episode that played by itself.

Movies and TV Shows have a banner of what's in progress, up next and new,
Recently Added, then every title as posters, sorted by name (in letter
sections with an index down the right), date added, release date or
rating; anime is left out. The Anime tab has its libraries, Continue
Watching, Next Up, Recently Added and the OP/ED plugin's videos. Library has
every library, favourites and playlists; a library, collection or playlist
opens as a sortable grid, and a cast member as their filmography. A grid
sorted by name loads whole; otherwise 100 at a time as it's scrolled. Each
page is one virtualized list, so a library of thousands scrolls smoothly.

Back: the page's arrow, the mouse's back button, Alt+← or Backspace.

Not yet: the sidebar's other tabs (Music, Audiobooks, Upcoming, Live TV,
Channels, Search, Settings), theme music and videos, disc menus, openings
and endings, playlists' own pages. Chinese titles sort under # in the letter
index; the Apple app files them by pinyin, which .NET has no table for.

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
