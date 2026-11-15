using HaPlayer.Ui;
using System.Text.Json;

namespace HaPlayer.Library;

public enum RepeatMode
{
    Off,
    All,
    One,

    /// <summary>По кругу только папку играющего трека.</summary>
    Folder,
}

/// <summary>Настройки в %APPDATA%\HaPlayer\settings.json.</summary>
/// <summary>Папка плейлистов: только группировка в медиатеке, сами плейлисты от неё не зависят.</summary>
public sealed class PlaylistFolder
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Expanded { get; set; } = true;
    public List<long> Playlists { get; set; } = [];
}

/// <summary>Условие умного плейлиста: поле, сравнение и значение. Поля и сравнения описаны в SmartFilter.</summary>
public sealed class SmartRule
{
    public string Field { get; set; } = "genre";
    public string Op { get; set; } = "contains";
    public string Value { get; set; } = "";
}

/// <summary>Умный плейлист: треки подбираются сами по условиям, через И (все) или ИЛИ (любое).</summary>
public sealed class SmartPlaylistDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool MatchAll { get; set; } = true;
    public List<SmartRule> Rules { get; set; } = [];
}

public sealed class Settings
{
    public double Volume { get; set; } = 80;
    public bool Shuffle { get; set; }
    public RepeatMode Repeat { get; set; } = RepeatMode.Off;

    // Эквалайзер по умолчанию выключен: плеер ничего не меняет в звуке без ведома пользователя.
    public bool EqEnabled { get; set; }
    public double EqPreamp { get; set; }
    public double[] EqGains { get; set; } = new double[10];
    public string EqPreset { get; set; } = L.T("Плоский");

    // Медиатека
    public List<string> MusicFolders { get; set; } = [];
    public bool RescanOnStart { get; set; } = true;
    public List<string> DisabledFormats { get; set; } = [];

    /// <summary>Куда сохраняется скачанное. Пусто — в папку с музыкой из медиатеки, где бы она ни лежала.</summary>
    public string? DownloadFolder { get; set; }

    /// <summary>Торренты выключены по умолчанию: пока выключено, плеер не открывает сетевых портов.</summary>
    public bool TorrentsEnabled { get; set; }

    // Оформление
    public string Palette { get; set; } = "haplayer";
    public string Surface { get; set; } = "dark";
    public bool HeaderFromCover { get; set; } = true;

    // Воспроизведение
    public string Output { get; set; } = "wasapi";
    public int LatencyMs { get; set; } = 100;

    // Сессия: что было открыто и что играло, когда закрыли окно
    public string LastPage { get; set; } = "home";
    public string LastTab { get; set; } = "Playlists";
    public List<string> LastQueue { get; set; } = [];
    public int LastIndex { get; set; } = -1;
    public double LastPosition { get; set; }
    public string? LastQueueTitle { get; set; }

    // Медиатека слева
    public bool LibrarySortAlpha { get; set; }
    public List<string> RecentCollections { get; set; } = [];
    public bool ShowQueuePanel { get; set; } = true;

    /// <summary>Закреплённые пользователем коллекции, ключи вида «album:…», в порядке закрепления.</summary>
    public List<string> PinnedCollections { get; set; } = [];

    /// <summary>Медиаклавиши и Ctrl+Alt+… работают, даже когда окно свёрнуто.</summary>
    public bool GlobalHotkeys { get; set; } = true;

    /// <summary>Компактный список треков: строки ниже, без обложек.</summary>
    public bool CompactList { get; set; }

    /// <summary>Показывать формат, битрейт, частоту и каналы под названием трека.</summary>
    public bool ShowTechInfo { get; set; }

    /// <summary>Показывать, сколько раз трек был прослушан.</summary>
    public bool ShowPlayCount { get; set; }

    /// <summary>Строгий режим: никаких своих альбомов, исполнителей и правок поверх тегов, только то, что в файлах.</summary>
    public bool StrictTags { get; set; }

    /// <summary>Возвращать очередь и трек при запуске.</summary>
    public bool RestoreQueue { get; set; } = true;

    /// <summary>Крестик окна прячет плеер в трей, музыка продолжает играть. Выход — из меню значка в трее.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Проверять новые версии на GitHub при запуске. Сеть без спроса не трогаем, поэтому выключено.</summary>
    public bool CheckUpdates { get; set; }

    /// <summary>Вопрос «Проверять обновления?» уже задан. Задаётся один раз, при первом запуске 1.8 и новее.</summary>
    public bool UpdatesAsked { get; set; }

    /// <summary>Свой акцентный цвет (#RRGGBB) поверх палитры. Пусто — цвет палитры.</summary>
    public string? CustomAccent { get; set; }

    /// <summary>Вести историю прослушивания (хранится только на этом компьютере).</summary>
    public bool KeepHistory { get; set; } = true;

    /// <summary>Устройство вывода звука. Пусто — устройство Windows по умолчанию.</summary>
    public string? OutputDeviceId { get; set; }

    /// <summary>
    /// Язык интерфейса: "en" или "ru". null в файле от версии до 1.6 — значит, человек уже пользовался
    /// русским интерфейсом. Новая установка начинает с английского.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>Масштаб интерфейса: 0.85, 1, 1.15 или 1.3.</summary>
    public double UiScale { get; set; } = 1;

    /// <summary>Шрифт интерфейса: Inter (встроенный), Segoe UI или Bahnschrift.</summary>
    public string UiFont { get; set; } = "Inter";

    /// <summary>Сила скруглений: 1 обычные, 0.5 меньше, 0 без скруглений.</summary>
    public double UiRadius { get; set; } = 1;

    /// <summary>Плотный вид: меньше обложки, шапки и отступы, строки списка компактные.</summary>
    public bool Dense { get; set; }

    /// <summary>Оценки треков от 1 до 5: звёзды в списке и пункт в меню трека. По умолчанию выключено.</summary>
    public bool ShowRatings { get; set; }

    /// <summary>Жанры всех треков уже прочитаны (однократный проход после обновления до 1.8).</summary>
    public bool GenresRead { get; set; }

    /// <summary>Плавный переход между треками, секунды. 0 — без паузы и без наложения.</summary>
    public double CrossfadeSeconds { get; set; }

    /// <summary>Визуализатор в «Сейчас играет» и мини-плеере.</summary>
    public bool ShowVisualizer { get; set; } = true;

    /// <summary>Порядок разделов главной: quick (плитки), smart (подборки), recent (недавно добавленные).</summary>
    public List<string> HomeOrder { get; set; } = ["quick", "smart", "recent"];

    /// <summary>Скрытые разделы главной.</summary>
    public List<string> HomeHidden { get; set; } = [];

    /// <summary>Альбомы и исполнители, которые не показываются на главной и в подборках («album:ключ», «artist:ключ»).</summary>
    public List<string> HiddenFromHome { get; set; } = [];

    /// <summary>Треки, которые не попадают в подборки, радио и перемешивание (пути файлов).</summary>
    public List<string> Blacklist { get; set; } = [];

    /// <summary>Закреплённые вверху плейлиста треки: «id плейлиста|путь», в порядке закрепления.</summary>
    public List<string> PinnedTracks { get; set; } = [];

    /// <summary>Плашка при смене трека, как в Winamp. По умолчанию выключена.</summary>
    public bool TrackToast { get; set; }

    /// <summary>Угол экрана для плашки: bottomright, topright, bottomleft, topleft.</summary>
    public string TrackToastCorner { get; set; } = "bottomright";

    /// <summary>Своя картинка на фоне окна (копия в папке данных). null — обычный фон.</summary>
    public string? BackgroundImage { get; set; }

    /// <summary>Затемнение картинки фона, 0..0.9.</summary>
    public double BackgroundDim { get; set; } = 0.45;

    /// <summary>Непрозрачность панелей поверх картинки фона, 0.4..1.</summary>
    public double PanelOpacity { get; set; } = 0.8;

    /// <summary>Шапки страниц: «kind:key» → color, abstract или image:путь.</summary>
    public Dictionary<string, string> Banners { get; set; } = [];

    /// <summary>Порядок альбомов на странице исполнителя, если его поменяли перетаскиванием: ключ исполнителя → ключи альбомов.</summary>
    public Dictionary<string, List<string>> AlbumOrder { get; set; } = [];

    /// <summary>Папки плейлистов в медиатеке слева.</summary>
    public List<PlaylistFolder> PlaylistFolders { get; set; } = [];

    /// <summary>Умные плейлисты пользователя: условия, по которым набираются треки.</summary>
    public List<SmartPlaylistDef> SmartPlaylists { get; set; } = [];

    /// <summary>Плейлисты, связанные с файлом .m3u8 в общей папке: id плейлиста → путь к файлу.</summary>
    public Dictionary<string, string> LinkedPlaylists { get; set; } = [];

    /// <summary>Вид окна: modern или classic. null — первый запуск, вид ещё не выбран.</summary>
    public string? ViewMode { get; set; }

    /// <summary>Классический вид: что показывает дерево слева, открытые вкладки и текущая.</summary>
    public string ClassicTreeMode { get; set; } = "Artists";
    public List<string> ClassicTabs { get; set; } = [];
    public string? ClassicActiveTab { get; set; }

    /// <summary>Плавное затухание при паузе и нарастание при снятии с паузы, мс. 0 — сразу.</summary>
    public int PauseFadeMs { get; set; }

    /// <summary>Пауза, когда пропало устройство вывода: выдернули наушники, отключился Bluetooth.</summary>
    public bool PauseOnDisconnect { get; set; } = true;

    /// <summary>Ручной режим: трек доигрывает, следующий встаёт на паузу и ждёт нажатия.</summary>
    public bool ManualAdvance { get; set; }

    /// <summary>Время изменения связанного файла после последней синхронизации, UTC ticks. Другое время — файл поменяли не мы.</summary>
    public Dictionary<string, long> LinkedStamps { get; set; } = [];

    // Окно
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    private string _file = "";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load(string file)
    {
        Settings s;
        try
        {
            if (File.Exists(file))
            {
                s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(file)) ?? new();
                s.Language ??= "ru";
                // Кто обновился со старой версии, вид уже видел: экран выбора ему не показываем.
                s.ViewMode ??= "modern";
            }
            else
            {
                s = new() { Language = "en" };
            }
        }
        catch
        {
            // Файл не читается. Прежде чем работать с настройками по умолчанию (и потом сохранить их поверх),
            // откладываем испорченный файл в settings.json.bak: его можно будет посмотреть или починить руками.
            try
            {
                File.Copy(file, file + ".bak", overwrite: true);
            }
            catch
            {
                // не удалось сделать копию: работаем дальше
            }
            s = new();
        }
        s._file = file;
        return s;
    }

    /// <summary>Пишет копию настроек в другой файл (при переносе данных).</summary>
    public void SaveCopy(string file) => File.WriteAllText(file, JsonSerializer.Serialize(this, Json));

    public void Save()
    {
        try
        {
            File.WriteAllText(_file, JsonSerializer.Serialize(this, Json));
        }
        catch
        {
            // настройки не критичны: не удалось сохранить, значит при следующем запуске будут по умолчанию
        }
    }
}
