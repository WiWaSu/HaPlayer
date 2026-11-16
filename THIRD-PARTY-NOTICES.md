# Third-party software

HaPlayer itself is under the MIT License (see `LICENSE`). It is built with the libraries below, each under its own license. `HaPlayer.exe` contains them inside one file.

| Library | Used for | License |
|---|---|---|
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) (Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Skia) | Interface | MIT |
| [SkiaSharp](https://github.com/mono/SkiaSharp), [HarfBuzzSharp](https://github.com/mono/SkiaSharp) | Drawing and text | MIT |
| [Inter](https://github.com/rsms/inter) font, via Avalonia.Fonts.Inter | Interface font | SIL Open Font License 1.1 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | Interface logic | MIT |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | Library database | MIT |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) | SQLite engine for the database | Apache-2.0 |
| [SQLite](https://sqlite.org) | The database engine itself | Public domain |
| [NAudio](https://github.com/naudio/NAudio) | Sound output, decoding | MIT |
| [NAudio.Vorbis](https://github.com/naudio/Vorbis), [NVorbis](https://github.com/NVorbis/NVorbis) | Ogg Vorbis | MIT |
| [TagLibSharp](https://github.com/mono/taglib-sharp) | Reading tags and covers | LGPL-2.1 |
| [MonoTorrent](https://github.com/alanmcgovern/monotorrent) | Optional torrent downloads | MIT |
| [.NET runtime](https://github.com/dotnet/runtime) | Runs the program | MIT |

## TagLibSharp and the LGPL

TagLibSharp is licensed under the GNU Lesser General Public License 2.1. Its source code is at https://github.com/mono/taglib-sharp. HaPlayer uses it unmodified (version 2.3.0 from NuGet). HaPlayer's own source code is published on GitHub, so you can build HaPlayer with a different or modified version of TagLibSharp: change the version in `src/HaPlayer/HaPlayer.csproj` and run `build.cmd`.

## Licenses in full

The full texts come with each library: on its GitHub page and inside its NuGet package. MIT, Apache-2.0, LGPL-2.1 and SIL OFL 1.1 are also at https://opensource.org/licenses.

---

# Сторонние библиотеки

Сам HaPlayer распространяется под лицензией MIT (файл `LICENSE`). Он собран из библиотек, перечисленных в таблице выше, у каждой своя лицензия. `HaPlayer.exe` содержит их внутри одного файла.

TagLibSharp, которая читает теги и обложки, распространяется под LGPL-2.1. Её код лежит на https://github.com/mono/taglib-sharp, HaPlayer использует её без изменений (версия 2.3.0 из NuGet). Код HaPlayer открыт на GitHub, поэтому его можно собрать с другой или изменённой версией TagLibSharp: поменять версию в `src/HaPlayer/HaPlayer.csproj` и запустить `build.cmd`.
