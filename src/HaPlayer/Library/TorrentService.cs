using MonoTorrent;
using MonoTorrent.Client;

namespace HaPlayer.Library;

/// <summary>
/// Торренты: скачать раздачу с музыкой по magnet-ссылке или .torrent-файлу и раздать свою папку друзьям.
/// Создаётся, только когда пользователь включил торренты в настройках. Пока выключено, плеер не открывает
/// ни одного сетевого порта. Общее решение без привязки к сайтам: поиска раздач и встроенных каталогов нет.
/// Файлы пользователя торрент-клиент никогда не удаляет, даже когда раздачу убирают из списка.
/// </summary>
public sealed class TorrentService
{
    // Открытые анонс-серверы: через них друг быстрее находит вашу раздачу. Без них работает только DHT,
    // а поиск через DHT может занять несколько минут.
    public static readonly string[] DefaultTrackers =
    [
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.torrent.eu.org:451/announce",
    ];

    private ClientEngine? _engine;
    private readonly bool _publicNetwork;

    private readonly int _listenPort;

    /// <param name="publicNetwork">false — без DHT и проброса портов на роутере, только для проверок на одном компьютере.</param>
    /// <param name="listenPort">Порт для входящих подключений. 0 — любой свободный.</param>
    public TorrentService(bool publicNetwork = true, int listenPort = 0)
    {
        _publicNetwork = publicNetwork;
        _listenPort = listenPort;
    }

    private static string Directory => Path.Combine(AppPaths.DataDirectory, "torrents");
    private static string StateFile => Path.Combine(Directory, "engine.state");

    // Свои раздачи. В них плеер никогда ничего не скачивает: только отдаёт то, что лежит в папке.
    // Иначе, если файл в папке изменится, клиент «починил» бы его чужой копией поверх ваших данных.
    private static string SharesFile => Path.Combine(Directory, "shares.txt");
    private readonly HashSet<string> _shares = new(StringComparer.OrdinalIgnoreCase);

    public bool IsRunning => _engine != null;
    public IReadOnlyList<TorrentManager> Torrents => _engine?.Torrents.ToList() ?? [];

    public async Task StartAsync()
    {
        if (_engine != null) return;
        System.IO.Directory.CreateDirectory(Directory);

        ClientEngine? engine = null;
        if (File.Exists(StateFile))
        {
            try
            {
                engine = await ClientEngine.RestoreStateAsync(StateFile);
            }
            catch
            {
                // Состояние не читается (например, после обновления библиотеки). Начинаем с пустого списка,
                // а испорченный файл откладываем, чтобы не потерять его совсем.
                TryMove(StateFile, StateFile + ".bak");
            }
        }
        var settings = new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(Directory, "cache"),
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            AutoSaveLoadDhtCache = true,
            AllowPortForwarding = _publicNetwork,
            AllowLocalPeerDiscovery = _publicNetwork,
            // UsePartialFiles не включаем: с ним свои раздачи ищут файлы под временными именами и не находят их.
            // Цена: от ненужного файла чужой раздачи (фильм, текст) может остаться обрывок размером с один кусок.
            // В медиатеку он не попадает, потому что это не аудио.
        };
        var address = _publicNetwork ? System.Net.IPAddress.Any : System.Net.IPAddress.Loopback;
        if (!_publicNetwork) settings.DhtEndPoint = null;
        if (!_publicNetwork || _listenPort > 0)
            settings.ListenEndPoints = new() { ["ipv4"] = new System.Net.IPEndPoint(address, _listenPort) };
        // Восстановленный клиент приходит со старыми настройками из файла состояния.
        // Подставляем текущие, чтобы изменения в новых версиях плеера действовали и на старые раздачи.
        if (engine != null) await engine.UpdateSettingsAsync(settings.ToSettings());
        else engine = new ClientEngine(settings.ToSettings());

        _engine = engine;
        LoadShares();
        foreach (var m in engine.Torrents)
        {
            if (IsShare(m))
            {
                // Свои раздачи проверяются заново в фоне: файлы в папке могли измениться, пока плеер был закрыт.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await StartShareAsync(m);
                    }
                    catch
                    {
                        // папку удалили или диск не подключён: раздача покажет ошибку в списке
                    }
                });
            }
            else
            {
                Watch(m);
                await m.StartAsync();
            }
        }
    }

    /// <summary>Сохраняет список раздач и останавливает клиент. Файлы и раздачи никуда не деваются.</summary>
    public async Task StopAsync()
    {
        var engine = _engine;
        if (engine == null) return;
        _engine = null;
        try
        {
            await engine.StopAllAsync(TimeSpan.FromSeconds(3));
            await engine.SaveStateAsync(StateFile);
        }
        finally
        {
            engine.Dispose();
        }
    }

    /// <summary>Достаёт magnet-ссылку из вставленного текста.</summary>
    public static bool TryParseMagnet(string text, out MagnetLink magnet)
    {
        magnet = null!;
        foreach (var part in text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var s = part.Trim().Trim('"', '\'', '<', '>');
            if (s.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) && MagnetLink.TryParse(s, out var m) && m != null)
            {
                magnet = m;
                return true;
            }
        }
        return false;
    }

    public async Task<TorrentManager> AddMagnetAsync(MagnetLink magnet, string saveFolder)
    {
        var engine = Engine();
        if (engine.Contains(magnet.InfoHashes)) throw new LinkException("Эта раздача уже есть в списке.");
        System.IO.Directory.CreateDirectory(saveFolder);
        var m = await engine.AddAsync(magnet, saveFolder);
        Watch(m);
        await m.StartAsync();
        return m;
    }

    public async Task<TorrentManager> AddTorrentFileAsync(string path, string saveFolder)
    {
        var engine = Engine();
        if (!Torrent.TryLoad(path, out var torrent) || torrent == null)
            throw new LinkException("Этот файл не похож на .torrent или он повреждён.");
        if (engine.Contains(torrent)) throw new LinkException("Эта раздача уже есть в списке.");
        System.IO.Directory.CreateDirectory(saveFolder);
        var m = await engine.AddAsync(torrent, saveFolder);
        Watch(m);
        await m.StartAsync();
        return m;
    }

    /// <summary>
    /// Создаёт раздачу из папки (или одного файла) и начинает её раздавать.
    /// Возвращает magnet-ссылку для друзей.
    /// </summary>
    public async Task<(TorrentManager Manager, string Magnet)> ShareAsync(string path, CancellationToken ct = default)
    {
        var engine = Engine();
        var source = new TorrentFileSource(path, ignoreHidden: true);
        if (!source.Files.Any()) throw new LinkException("В этой папке нет файлов.");

        var creator = new TorrentCreator(TorrentType.V1Only) { CreatedBy = "HaPlayer" };
        creator.Announces.Add([.. DefaultTrackers]);
        var torrent = Torrent.Load(await creator.CreateAsync(source, ct));

        var existing = engine.Torrents.FirstOrDefault(t => t.InfoHashes.Equals(torrent.InfoHashes));
        if (existing != null) return (existing, MagnetFor(existing));

        // Раздаём файлы прямо оттуда, где они лежат: ничего не копируется.
        // Раздача из нескольких файлов ищет их в «<место>\<имя раздачи>\…», а из одного файла — прямо в «<место>\файл».
        // Папка с единственной песней превращается во второй вариант, поэтому место для неё — сама папка.
        var full = Path.GetFullPath(path).TrimEnd('\\');
        var saveDir = System.IO.Directory.Exists(full) && torrent.Files.Count == 1 ? full : Path.GetDirectoryName(full)!;
        var m = await engine.AddAsync(torrent, saveDir);
        _shares.Add(Key(m));
        SaveShares();
        await StartShareAsync(m);
        return (m, MagnetFor(m));
    }

    /// <summary>Убирает раздачу из списка. Скачанные и свои файлы остаются на диске.</summary>
    public async Task RemoveAsync(TorrentManager m)
    {
        var engine = Engine();
        await m.StopAsync(TimeSpan.FromSeconds(3));
        await engine.RemoveAsync(m, RemoveMode.CacheDataOnly);
        if (_shares.Remove(Key(m))) SaveShares();
    }

    public static string MagnetFor(TorrentManager m)
    {
        var trackers = m.Torrent?.AnnounceUrls.SelectMany(t => t).Distinct().ToList() ?? [.. DefaultTrackers];
        if (trackers.Count == 0) trackers = [.. DefaultTrackers];
        return new MagnetLink(m.InfoHashes, m.Torrent?.Name ?? m.Name, trackers, null, m.Torrent?.Size).ToV1String();
    }

    public bool IsShare(TorrentManager m) => _shares.Contains(Key(m));

    /// <summary>Своя раздача, в которой часть файлов изменилась или пропала: эти куски не раздаются.</summary>
    public bool IsBrokenShare(TorrentManager m) =>
        IsShare(m) && m.HasMetadata && !m.Complete && m.State != TorrentState.Hashing;

    private static string Key(TorrentManager m) => m.InfoHashes.V1OrV2.ToHex();

    /// <summary>
    /// Запускает свою раздачу так, чтобы в папку не записалось ни байта:
    /// 1) проверяем файлы, пока раздача не запущена (со всеми включёнными файлами, иначе проверка пропустит
    ///    запрещённые и решит, что их нет);
    /// 2) запрещаем скачивание всех файлов;
    /// 3) запускаем. Раздаётся всё, что совпало при проверке.
    /// </summary>
    private static async Task StartShareAsync(TorrentManager m)
    {
        foreach (var f in m.Files)
            if (f.Priority == Priority.DoNotDownload)
                await m.SetFilePriorityAsync(f, Priority.Normal);
        await m.HashCheckAsync(autoStart: false);
        foreach (var f in m.Files)
            await m.SetFilePriorityAsync(f, Priority.DoNotDownload);
        await m.StartAsync();
    }

    private void LoadShares()
    {
        _shares.Clear();
        try
        {
            if (File.Exists(SharesFile))
                foreach (var line in File.ReadAllLines(SharesFile))
                    if (line.Trim().Length > 0) _shares.Add(line.Trim());
        }
        catch
        {
            // нечитаемый список: раздачи останутся, но без защиты от докачки до следующего «Раздать папку»
        }
    }

    private void SaveShares()
    {
        try
        {
            File.WriteAllLines(SharesFile, _shares);
        }
        catch
        {
            // не удалось записать: защита действует до перезапуска
        }
    }

    public static bool IsWanted(string path) => AudioFormats.IsSupported(path) || AppPaths.IsImage(path);

    public static bool HasMusic(TorrentManager m) => m.Files.Any(f => AudioFormats.IsSupported(f.DownloadCompleteFullPath));

    /// <summary>
    /// Скачано всё, что нужно. Флаг Complete тут не подходит: он ждёт и отключённые файлы (фильмы, тексты),
    /// которые мы специально не качаем.
    /// </summary>
    public static bool IsFinished(TorrentManager m) =>
        m.HasMetadata && (m.Complete || m.Files.Where(f => f.Priority != Priority.DoNotDownload).All(f => f.BitField.AllTrue));

    /// <summary>Готовые аудиофайлы раздачи на диске.</summary>
    public static List<string> MusicFiles(TorrentManager m) =>
        m.Files.Where(f => f.Priority != Priority.DoNotDownload && f.BitField.AllTrue && AudioFormats.IsSupported(f.DownloadCompleteFullPath))
            .Select(f => f.DownloadCompleteFullPath)
            .Where(File.Exists)
            .ToList();

    private ClientEngine Engine() =>
        _engine ?? throw new LinkException("Торренты выключены. Включите их в настройках.");

    /// <summary>
    /// Когда известен список файлов, отключаем всё, кроме музыки и обложек.
    /// Если музыки в раздаче нет совсем, ставим её на паузу: качать фильмы и программы плеер не будет.
    /// </summary>
    private void Watch(TorrentManager m)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (!m.HasMetadata) await m.WaitForMetadataAsync();
                if (m.Complete) return; // своя раздача или уже скачано
                foreach (var f in m.Files)
                    if (!IsWanted(f.DownloadCompleteFullPath) && f.Priority != Priority.DoNotDownload)
                        await m.SetFilePriorityAsync(f, Priority.DoNotDownload);
                if (!HasMusic(m)) await m.StopAsync();
            }
            catch
            {
                // раздачу убрали раньше, чем пришёл список файлов
            }
        });
    }

    private static void TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
        }
        catch
        {
            // не удалось: при следующем сохранении файл перезапишется
        }
    }
}
