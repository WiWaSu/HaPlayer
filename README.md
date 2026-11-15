<p align="center">
  <img src="assets/branding/banner.png" alt="HaPlayer" width="800">
</p>

<p align="center">
  Music player for the files on your own drive. Windows, offline, no account, no telemetry.<br>
  <b>English</b> · <a href="README.ru.md">Русский</a>
</p>

## Download

Get `HaPlayer.exe` from **Releases**. Windows 10 or 11, 64-bit. No need to install .NET.
The portable zip keeps all data next to the program.

## Features

- Library from folders and files, tags and cover art. Rescans read only changed files.
- MP3, FLAC, WAV, Ogg Vorbis, AAC/M4A, ALAC, WMA.
- Playlists in folders, smart playlists by conditions (AND / OR), Liked Songs, queue with "Play next", shuffle without repeats.
- Playlist import from Winamp, AIMP, VLC, Windows Media Player, iTunes.
- Two views, switchable any time: modern (large covers, mixes, roomy pages) and classic (a time display, a library tree, tabs and a dense track table).
- Album and artist pages, search, full-window "Now playing".
- 10-band equalizer, output device switching, listening history.
- English and Russian interface. Dark, graphite and light themes, color palettes, your own accent color, interface scale and font.
- Mini player on top of other windows, media keys that work while minimized, tray icon.
- Mixes: new in library, on repeat, haven't heard in a while. Search filters by folder, year and format.
- Drag tracks onto playlists and into the queue. Export any list to .m3u8 with full or relative paths, or link a playlist to an .m3u8 file in a shared folder.
- The layout adapts to the window, down to 640×480.
- Optional: download by direct link, torrents. Both off by default.

HaPlayer never changes your files or their tags. It deletes a file only when you pick "Delete file", and then to the Recycle Bin. Its data lives in `%APPDATA%\HaPlayer` (or next to the program in portable mode).

## Build

Windows 10/11 and [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0):

```bash
dotnet run --project src/HaPlayer
```

`build.cmd` makes a single-file `HaPlayer.exe`.

Built with C#, [Avalonia](https://avaloniaui.net/), [NAudio](https://github.com/naudio/NAudio), [TagLib#](https://github.com/mono/taglib-sharp), SQLite, [MonoTorrent](https://github.com/alanmcgovern/monotorrent).

## License

Not chosen yet. Until then, all rights reserved by the author.
