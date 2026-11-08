<p align="center">
  <img src="assets/branding/banner.png" alt="HaPlayer" width="800">
</p>

<p align="center">
  A native music player for Windows that plays the files on your disk.<br>
  No account, no cloud, no telemetry.
</p>

<p align="center">
  <b>English</b> · <a href="README.ru.md">Русский</a>
</p>

---

> [!WARNING]
> HaPlayer is in early development. Things break and change between commits. There is no release build yet.

## Why

Streaming made music easy to find and easy to lose. Tracks disappear from catalogs, subscriptions get more expensive, and your listening history lives on someone else's server. Local players solve that, but most of them look like 2005 or need an hour of setup.

HaPlayer aims for streaming-app comfort on top of your own files: pick a music folder, wait a few seconds, press play.

## Principles

- **Your files stay yours.** HaPlayer only reads them. It never renames, moves, deletes or rewrites tags on its own.
- **Offline by default.** Scanning, search, playback and playlists work without a network connection.
- **No tracking.** No telemetry, no analytics, no ads. Everything HaPlayer knows about your library sits in `%APPDATA%\HaPlayer`: `library.db` (SQLite) and `settings.json`.
- **Native.** No Electron, no WebView. The UI is drawn by Avalonia, the audio goes through WASAPI.

The full reasoning is in [docs/PHILOSOPHY.md](docs/PHILOSOPHY.md) (in Russian).

## Features

What works now:

- Library scan with tags and cover art
- Playlists, shuffle, repeat
- Import of Winamp playlists
- Download by a direct link to an audio file, from any site (Settings → Download by link). Only what you paste, nothing is fetched on its own
- Torrents, off by default: download music by a magnet link or a .torrent file (only audio and cover images are fetched), and share your own folder with friends with one button. HaPlayer never writes into a folder you share
- Add an artist from a discography folder: every song inside goes to one artist, even if the tags disagree. Files are not changed
- Formats: MP3, FLAC, WAV, Ogg Vorbis, AAC/M4A, WMA

Planned:

- [ ] Opus, ALAC, APE, WavPack via FFmpeg
- [ ] Bit-perfect output (WASAPI exclusive mode)
- [ ] Gapless playback
- [ ] ReplayGain
- [ ] Optional sync between devices with end-to-end encryption and no email or phone number
- [ ] macOS and Linux builds

## Building from source

You need Windows 10 or 11 and the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```bash
git clone https://github.com/<user>/haplayer.git
cd haplayer
dotnet run --project src/HaPlayer
```

To open the project in an IDE, use `HaPlayer.sln` with Visual Studio 2022 or JetBrains Rider.

## Tech stack

| Part | Library |
|---|---|
| Language | C# / .NET 9 |
| UI | [Avalonia UI](https://avaloniaui.net/) 11 |
| Audio output | [NAudio](https://github.com/naudio/NAudio) (WASAPI) |
| Ogg Vorbis | [NAudio.Vorbis](https://github.com/naudio/Vorbis) |
| Tags and covers | [TagLib#](https://github.com/mono/taglib-sharp) |
| Library storage | SQLite via Microsoft.Data.Sqlite |
| Torrents | [MonoTorrent](https://github.com/alanmcgovern/monotorrent) |
| MVVM | CommunityToolkit.Mvvm |

## Project layout

```
src/HaPlayer/
  Audio/        playback engine
  Library/      scanning, tags, covers, SQLite database, settings
  ViewModels/   UI state
  Views/        Avalonia windows and controls
  Styles/       theme
assets/branding/  logo, app icon, banner
docs/             project philosophy
```

## License

Not chosen yet. A license will be added before the first public release. Until then, all rights are reserved by the author.
