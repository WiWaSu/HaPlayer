using HaPlayer.Ui;
using System.Text.Json;

namespace HaPlayer.Library;

public enum RepeatMode
{
    Off,
    All,
    One,
}

/// <summary>Настройки в %APPDATA%\HaPlayer\settings.json.</summary>
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
