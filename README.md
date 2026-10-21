# Aster for Windows

An Avalonia port of Aster Media Player, in progress. The Apple app in the
folders above is the reference: `Aster.Core` ports its `Jellyfin/` layer
file by file, so keep the two in step when either changes.

| Project | What it is |
| --- | --- |
| `src/Aster.Core` | Jellyfin client, models, device profile, resume points, session storage (Windows Credential Manager), subtitle and trickplay helpers, JellyDisc models, audiobooks (grouping, chapters, speeds) |
| `src/Aster.Mpv` | Bindings for libmpv: the player, its events, and OpenGL rendering into the UI |
| `src/Aster.Desktop` | The Avalonia app: sign-in, Home, Movies, TV Shows, Anime, Music, Audiobooks, Library, title, person, album, artist and book pages, the video player, and the music and audiobook players |
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
- `--tab <search|movies|shows|anime|music|audiobooks|upcoming|livetv|channels|library|settings>` starts on a tab; `--sort <Name|DateAdded|ReleaseDate|Rating>` sorts it.
- Signing out of such a session only ends it; the saved sign-in is left alone.
- `--play <itemId>` plays a title straight away, from its resume point;
  `--start <seconds>` and `--max-bitrate <bps>` change where it starts and
  cap its quality (which makes the server convert it).
- `ASTER_LOG=<file>` writes the app's and mpv's messages to a file:
  which stream was chosen, the track mapping, the decoder, dropped frames.
- `ASTER_MUTE=1` mutes mpv, for trying playback without the sound.

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

Search (or Ctrl+F) looks as typing pauses: movies and shows as a grid,
then shelves of episodes, albums and playlists, artists and songs; a song
plays the songs found, from it.

Not yet: theme
music and videos, disc menus, and openings and endings. Chinese titles sort
under # in the letter index; the Apple app files them by pinyin, which .NET
has no table for.

## Upcoming and Live TV

Upcoming shows when shows in the library have episodes airing soon: a
banner of the next to air, with a countdown ("Tomorrow"), then a row for
each day. Most are placeholders from the show's schedule, so a card has the
show's picture and logo, and opens the show; one already on the server is
marked Available and opens itself.

Live TV shows when the server has it, in four sections:

- **On Now:** what's airing, then Movies, Sports, Kids and News, and the
  favourite channels.
- **Guide:** a row per channel (favourites first, then by number), its
  programmes laid out along a timeline from the current half hour to a day
  ahead. Earlier and Later (or Shift and the wheel, or a sideways swipe)
  move through time; Now comes back. Rows load their listings as they
  scroll into view.
- **Channels:** every channel as a logo; a right-click makes it a
  favourite.
- **Recordings:** what's been recorded, what's scheduled, and the series
  rules, each rule with its own options page (new episodes only, any
  channel or time, how many to keep and for how long, padding).

A programme's page has Watch (or Watch Channel, later on), Record or Cancel
Recording, Record Series or Series Options, and the channel's favourite. A
channel plays the server's live stream in the player, with LIVE in place of
the timeline; its captions follow the subtitle setting, on only when
subtitles are always on.

Not yet: the Apple app's mini player, which keeps a channel playing in the
Live TV header after leaving full screen.

## Channels (Beta)

Channels are Jellyfin video playlists tagged as channels
([docs/channel-tags.md](../docs/channel-tags.md)), so the server is the one
place they live and every device finds the same ones. The tab shows when
there are any: the Live TV guide, a week of it with a day picker, each
channel's schedule worked out from the clock rather than fetched. What's on
now tunes in; anything later opens its page.

`Aster.Core/Channels` is a port of the Apple app's channel code, kept to it
exactly so a channel shows the same thing at the same moment on a Mac, an
Apple TV and a PC: the tags (read and written as
`AsterFINTests/Conformance/channel-tags.json` says, which the tests load),
and the schedule (FNV-1a seeds, SplitMix64 shuffles, times as Swift's `Date`
keeps them, the Gregorian calendar in the local zone), checked against
lineups the Apple app worked out. Rotation, Blocks, Shuffle, In Order and
Air Date; daily, weekly or monthly reshuffles; seeds, weights and lengths;
genres, networks and collections as sources; and breaks of bumpers, promos,
commercials, trailers and interstitials, lined up on the mark or not.

On a channel the player follows the schedule: no timeline or seeking, the
channel's name in place of it, no markers or Up Next, and nothing reported,
so resume points and watched state stay as they were. When a title ends,
what the channel shows now plays; PageUp and PageDown change channel. The
channel's bug sits in its corner of the picture, steps aside for breaks,
and fades after `bug-hide` seconds.

Not yet: Edit Channels (an administrator's page for making channels and
setting their tags; the Apple app or Jellyfin's own Edit Metadata ▸ Tags
does it meanwhile).

## Settings

Settings has the Apple app's Playback, Audio & Subtitles, Account and
About pages on one page, as the Mac's Settings window has them:

- **On this computer** (`%LOCALAPPDATA%\Aster\settings.json`): the
  maximum streaming quality, and playing videos full screen (on, as on the
  Mac; the window comes back as the player closes).
- **Following the user** between devices and the Apple app: autoplaying
  the next episode, skipping intros, and drawing ASS/SSA subtitles styled
  (off, mpv strips them to plain text). They're fetched at sign-in and
  when Settings opens, a change is saved as it's made, and a setting never
  chosen on this computer keeps the default rather than overwriting
  another device's choice.
- **The user's Jellyfin settings**, shared with the web app: the preferred
  audio and subtitle languages and when subtitles show.
- **Account:** the server, its address and Jellyfin version, and the user,
  with Switch User and Change Server (both sign out, after asking; Switch
  User keeps the server's address filled in).

Not yet: the Disc Menus, Title Pages and Channels pages, and the anime song
cards, which wait on those features.

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

Not yet: OP/ED song cards, trickplay previews,
chapters, downloads, and HDR passthrough (HDR is tone-mapped to SDR, since
the video is drawn through OpenGL so the controls can sit on top of it).

## Music and audiobooks

The Music and Audiobooks tabs show when the server has a music or books
library. Music has Albums (Recently Added, Recently Played, Favorite Albums,
then every album, sortable), Artists, Playlists and Songs. An album or
playlist page lists its tracks by disc, with Play, Shuffle, Instant Mix and
favourite; a playlist's tracks can be moved and removed from their menu. An
artist's page has their albums.

Audiobooks are grouped as the Apple app groups them: a book is a folder of
parts, its author and series taken from the folders above it. The tab has a
banner of books in progress and new, Continue Listening, Recently Added,
then every book, or by author or series. A book's page has its chapters
(from the files' chapter marks, or one per part), Play or Resume and From
the Beginning.

Both play through a second, headless mpv, never at the same time as each
other or a video. While one plays, the sidebar shows it, with play/pause
and stop; clicking it opens the full player: for a song, its artwork,
seeking, shuffle, repeat, synced lyrics when the server has them, and Up
Next; for a book, the chapter's progress and the time left in the book, back
15 and ahead 30 seconds, chapters, and a speed kept per book (in
`%LOCALAPPDATA%\Aster\audiobook-rates.json`). Space or the keyboard's
play/pause key pauses either; Esc closes the full player. Progress goes to
the server every 10 seconds, so a book resumes where it was left on any
device.

Not yet: the Windows media overlay and media keys beyond play/pause, Add to
Playlist, downloads, and the sleep timer.

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
