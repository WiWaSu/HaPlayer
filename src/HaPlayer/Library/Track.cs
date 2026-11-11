using HaPlayer.Ui;
namespace HaPlayer.Library;

public sealed class Track
{
    public required string Path { get; init; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string? AlbumArtist { get; set; }
    public int TrackNo { get; set; }
    public int DiscNo { get; set; }
    public int Year { get; set; }
    public double DurationSeconds { get; set; }

    // Технические данные файла. 0 — ещё не прочитаны (трек добавлен версией до 1.6).
    public int Bitrate { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }

    /// <summary>Время изменения файла (UTC ticks). По нему повторное сканирование пропускает неизменённые файлы.</summary>
    public long ModifiedTicks { get; set; }

    /// <summary>Когда трек попал в медиатеку (UTC ticks).</summary>
    public long AddedTicks { get; set; }

    // Привязка, которую задал пользователь в HaPlayer. Хранится в базе, теги файла не меняются.
    public string? UserAlbumKey { get; set; }
    public string? UserArtist { get; set; }

    /// <summary>
    /// Строгий режим: альбом и исполнитель только из тегов файла, привязки из HaPlayer не учитываются.
    /// Сами привязки остаются в базе и вернутся, если режим выключить.
    /// </summary>
    public static bool TagsOnly { get; set; }

    public string EffectiveArtist => (TagsOnly ? null : UserArtist) ?? Artist;
    public string AlbumKey => (TagsOnly ? null : UserAlbumKey) ?? MakeAlbumKey(Album, AlbumArtist);
    public string EffectiveAlbum => !TagsOnly && UserAlbumKey != null ? AlbumNameFromKey(UserAlbumKey) : Album;

    public static string MakeAlbumKey(string album, string? albumArtist) => $"{album}\u001f{albumArtist ?? ""}";
    public static string AlbumNameFromKey(string key) => key.Split('\u001f')[0];
}

public static class AudioFormats
{
    // Только то, что сейчас умеет NAudioEngine. Opus, APE, WavPack и AIFF добавим вместе с FFmpeg.
    public static readonly string[] Extensions =
        [".mp3", ".flac", ".wav", ".ogg", ".oga", ".m4a", ".aac", ".wma"];

    /// <summary>Подписи для настроек: какие расширения к какому формату относятся.</summary>
    public static readonly (string Name, string Description, string[] Extensions)[] Groups =
    [
        ("MP3", L.T("Самый распространённый формат со сжатием"), [".mp3"]),
        ("FLAC", L.T("Сжатие без потерь"), [".flac"]),
        ("WAV", L.T("Несжатый звук"), [".wav"]),
        ("Ogg Vorbis", ".ogg, .oga", [".ogg", ".oga"]),
        ("AAC / M4A", L.T("iTunes, Apple Music, ALAC внутри .m4a"), [".m4a", ".aac"]),
        ("WMA", "Windows Media Audio", [".wma"]),
    ];

    private static HashSet<string> _enabled = new(Extensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>Отключённые в настройках форматы не попадают в медиатеку при сканировании.</summary>
    public static void SetDisabled(IEnumerable<string> disabled)
    {
        var off = new HashSet<string>(disabled, StringComparer.OrdinalIgnoreCase);
        _enabled = new HashSet<string>(Extensions.Where(e => !off.Contains(e)), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Файл подходит для медиатеки: формат поддерживается и не отключён.</summary>
    public static bool IsAudio(string path) => _enabled.Contains(System.IO.Path.GetExtension(path));

    /// <summary>Формат поддерживается движком (для протокола воспроизведения и импорта плейлистов).</summary>
    public static bool IsSupported(string path) =>
        Extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

public enum DataMode
{
    /// <summary>%APPDATA%\HaPlayer, как у установленных программ.</summary>
    AppData,

    /// <summary>Рядом с exe: программу с данными можно носить на флешке.</summary>
    Portable,

    /// <summary>В выбранной пользователем папке, например на диске с музыкой.</summary>
    Custom,
}

public static class AppPaths
{
    // Где лежат данные (база, настройки, фото). Выбирается один раз при запуске:
    //  1. рядом с exe есть файл HaPlayer.portable → портативный режим, папка «HaPlayer Data» рядом с программой;
    //  2. в %APPDATA%\HaPlayer\location.txt записана своя папка → данные там;
    //  3. иначе %APPDATA%\HaPlayer.

    public static string ExeDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HaPlayer");

    public static string PortableMarker => Path.Combine(ExeDirectory, "HaPlayer.portable");
    public static string PortableDirectory => Path.Combine(ExeDirectory, "HaPlayer Data");
    public static string PointerFile => Path.Combine(AppDataDirectory, "location.txt");

    private static string? _resolved;
    private static DataMode _mode;

    public static DataMode Mode
    {
        get
        {
            Resolve();
            return _mode;
        }
    }

    public static string DataDirectory
    {
        get
        {
            Resolve();
            Directory.CreateDirectory(_resolved!);
            return _resolved!;
        }
    }

    private static void Resolve()
    {
        if (_resolved != null) return;
        if (File.Exists(PortableMarker))
        {
            _mode = DataMode.Portable;
            _resolved = PortableDirectory;
            return;
        }
        try
        {
            if (File.Exists(PointerFile))
            {
                var custom = File.ReadAllText(PointerFile).Trim();
                var root = Path.GetPathRoot(custom);
                // Внешний диск может быть не подключён: тогда работаем из AppData.
                if (custom.Length > 0 && root != null && Directory.Exists(root))
                {
                    _mode = DataMode.Custom;
                    _resolved = custom;
                    return;
                }
            }
        }
        catch
        {
            // нечитаемый location.txt
        }
        _mode = DataMode.AppData;
        _resolved = AppDataDirectory;
    }

    /// <summary>Папка данных для режима. Для своей папки — подпапка «HaPlayer Data» внутри выбранной.</summary>
    public static string DirectoryFor(DataMode mode, string? customRoot = null) => mode switch
    {
        DataMode.Portable => PortableDirectory,
        DataMode.Custom => customRoot != null && Path.GetFileName(customRoot.TrimEnd('\\')) == "HaPlayer Data"
            ? customRoot
            : Path.Combine(customRoot ?? AppDataDirectory, "HaPlayer Data"),
        _ => AppDataDirectory,
    };

    /// <summary>Переключает режим: меняет метку и указатель. Сами данные копирует вызывающий код. Действует после перезапуска.</summary>
    public static void SetMode(DataMode mode, string? customDir = null)
    {
        if (mode == DataMode.Portable)
        {
            File.WriteAllText(PortableMarker, ("Этот файл включает портативный режим HaPlayer: данные хранятся в папке «HaPlayer Data» рядом с программой.\r\n"));
            return;
        }

        if (File.Exists(PortableMarker)) File.Delete(PortableMarker);
        if (mode == DataMode.Custom && customDir != null)
        {
            Directory.CreateDirectory(AppDataDirectory);
            File.WriteAllText(PointerFile, customDir);
        }
        else if (File.Exists(PointerFile))
        {
            File.Delete(PointerFile);
        }
    }

    /// <summary>Копии фотографий, которые пользователь поставил плейлистам, альбомам и исполнителям.</summary>
    public static string ImagesDirectory
    {
        get
        {
            var dir = Path.Combine(DataDirectory, "images");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".bmp"];

    public static bool IsImage(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
