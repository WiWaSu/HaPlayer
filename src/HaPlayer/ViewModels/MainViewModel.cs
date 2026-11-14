using HaPlayer.Ui;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlayer.Audio;
using HaPlayer.Library;

namespace HaPlayer.ViewModels;

/// <summary>Диалоги живут в окне. ViewModel получает их через этот интерфейс.</summary>
public interface IDialogService
{
    Task<IReadOnlyList<string>> PickFilesAsync();
    Task<string?> PickFolderAsync();
    Task<string?> PickImageAsync();
    Task<IReadOnlyList<string>> PickPlaylistFilesAsync();
    Task<string?> PickTorrentFileAsync();
    Task<string?> PickSaveFileAsync(string suggestedName, string extension);
    Task CopyTextAsync(string text);
    Task<string?> PromptAsync(string title, string placeholder, string initial, string okText);
    Task<bool> ConfirmAsync(string title, string message, string okText);
    Task MessageAsync(string title, string message);
}

public enum RightPanelMode
{
    None,
    NowPlaying,
    Devices,
    History,
}

public enum LibraryTab
{
    /// <summary>Фильтр не выбран: плейлисты, альбомы и исполнители вместе.</summary>
    All,
    Playlists,
    Albums,
    Artists,
    Folders,
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly LibraryDb _db;
    private readonly List<TrackItem> _library = [];
    private readonly Dictionary<string, TrackItem> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<(string Kind, string Key), string> _images;
    private List<PlaylistData> _playlistData;
    private readonly List<string> _likes;
    private bool _syncingSelection;
    private readonly HashSet<string> _hidden;

    public MainViewModel(LibraryDb db, Settings settings, IAudioEngine engine, CoverService covers)
    {
        _db = db;
        Settings = settings;
        Engine = engine;
        Covers = covers;
        Player = new PlayerViewModel(engine, settings) { LikeHandler = t => ToggleLike([t]) };
        Equalizer = new EqualizerViewModel(engine, settings);
        Torrents = new TorrentsViewModel(this);

        Track.TagsOnly = settings.StrictTags;
        foreach (var t in db.LoadAll()) AddTrackItem(new TrackItem(t));
        _notes = db.LoadNotes();
        var blacklist = settings.Blacklist.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _library) t.IsBlacklisted = blacklist.Contains(t.Path);
        foreach (var (path, stars) in db.LoadRatings())
            if (_byPath.TryGetValue(path, out var rated)) rated.Rating = stars;
        foreach (var (path, (count, last)) in db.LoadPlayStats())
            if (_byPath.TryGetValue(path, out var played))
            {
                played.PlayCount = count;
                played.LastPlayedTicks = last;
            }
        _images = db.LoadImages();
        _playlistData = db.LoadPlaylists();
        _likes = db.LoadLikes();
        // Сердечко у всех копий любимой песни, даже если лайкнута была только одна из них.
        var likedSongs = _likes.Select(p => _byPath.GetValueOrDefault(p)).OfType<TrackItem>().Select(t => t.SongKey).ToHashSet();
        foreach (var t in _library)
            if (likedSongs.Contains(t.SongKey)) t.IsLiked = true;
        _liked = new LikedItem([], covers, Open, Play);
        _allTracks = new AllTracksItem(_library, covers, Open, Play);
        _sortAlphabetical = settings.LibrarySortAlpha;
        _rightPanel = settings.ShowQueuePanel ? RightPanelMode.NowPlaying : RightPanelMode.None;
        _hidden = db.LoadHidden();
        if (!string.IsNullOrEmpty(settings.OutputDeviceId)) engine.SetDevice(settings.OutputDeviceId);
        Player.Listened += OnListened;
        Player.StoppedBySleepTimer += () =>
        {
            IsSleepActive = false;
            SleepLeft = "";
        };
        Player.QueueChanged += RefreshUpNext;
        _currentPage = new HomePageViewModel(this);
        Rebuild();

        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.Position)) UpdateLyricLine();
            if (e.PropertyName == nameof(PlayerViewModel.CurrentTrack))
            {
                _ = LoadLyricsAsync();
                _ = UpdateNowPlayingAsync();
                OnPropertyChanged(nameof(ShowQueuePanel));
            }
            if (e.PropertyName == nameof(PlayerViewModel.Queue)) MarkNowPlaying();
        };
    }

    public PlayerViewModel Player { get; }
    public EqualizerViewModel Equalizer { get; }
    public TorrentsViewModel Torrents { get; }
    public CoverService Covers { get; }
    public Settings Settings { get; }
    public IAudioEngine Engine { get; }
    public IDialogService? Dialogs { get; set; }

    public bool IsSettingsActive => CurrentPage is SettingsPageViewModel;

    /// <summary>Кнопка настроек работает как переключатель: открыть, а повторное нажатие закрывает.</summary>
    [RelayCommand]
    private void GoSettings()
    {
        if (CurrentPage is SettingsPageViewModel) CloseSettings();
        else CurrentPage = new SettingsPageViewModel(this);
    }

    /// <summary>Возвращает на страницу, с которой открыли настройки. Если её нет, на главную.</summary>
    public void CloseSettings()
    {
        if (CurrentPage is not SettingsPageViewModel) return;
        if (_back.Count > 0) GoBack();
        else CurrentPage = new HomePageViewModel(this);
    }

    public string SettingsTip => IsSettingsActive ? L.T("Закрыть настройки (Esc)") : L.T("Настройки");

    // ---------- Папки медиатеки ----------

    internal void RememberFolder(string folder)
    {
        var f = folder.TrimEnd('\\', '/');
        bool Covers(string parent, string child) =>
            child.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
            child.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

        if (Settings.MusicFolders.Any(x => Covers(x, f))) return;
        // Новая папка может включать уже добавленные вложенные: их убираем, чтобы не сканировать дважды.
        Settings.MusicFolders.RemoveAll(x => Covers(f, x));
        Settings.MusicFolders.Add(f);
        Settings.Save();
    }

    public async Task AddMusicFolderAsync()
    {
        if (Dialogs == null) return;
        var folder = await Dialogs.PickFolderAsync();
        if (folder == null) return;
        RememberFolder(folder);
        await ImportAsync([folder]);
    }

    /// <summary>Убирает папку из медиатеки. Файлы на диске остаются, плейлисты не меняются.</summary>
    public async Task RemoveMusicFolderAsync(string folder)
    {
        if (Dialogs == null) return;
        var prefix = folder.TrimEnd('\\') + "\\";
        var count = _library.Count(t => t.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var ok = await Dialogs.ConfirmAsync(L.T("Убрать папку из медиатеки?"),
            L.F("{0}\n\nИз HaPlayer исчезнут треки из этой папки: {1}. Файлы на диске останутся на месте.", folder, count), "Убрать");
        if (!ok) return;

        _db.RemoveUnder(folder);
        var removed = _library.Where(t => t.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var t in removed)
        {
            _library.Remove(t);
            _byPath.Remove(t.Path);
        }
        Settings.MusicFolders.RemoveAll(x => x.Equals(folder, StringComparison.OrdinalIgnoreCase));
        Settings.Save();
        Rebuild();
        RefreshPage();
    }

    /// <summary>Проверяет все папки медиатеки: новые и изменённые файлы перечитываются, остальные пропускаются.</summary>
    public async Task RescanAsync()
    {
        var folders = Settings.MusicFolders.Where(Directory.Exists).ToList();
        if (folders.Count > 0) await ImportAsync(folders, rescan: true);
    }

    /// <summary>Перекрашивает интерфейс: ресурсы меняются сразу, а обложки «Любимых» и шапки пересоздаются.</summary>
    // ---------- Где хранить данные ----------

    /// <summary>Окно включает или выключает плотный вид. Задаёт MainWindow.</summary>
    public Action<bool>? DensityChanged { get; set; }

    /// <summary>Окно меняет масштаб интерфейса. Задаёт MainWindow.</summary>
    public Action<double>? ScaleChanged { get; set; }

    /// <summary>Перезапуск программы. Задаёт App: ему виден жизненный цикл окна.</summary>
    public Action? RestartRequested { get; set; }

    // ---------- Обновления ----------

    /// <summary>Новая версия на GitHub, если нашлась. Тогда в верхней панели появляется кнопка.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateText))]
    private UpdateService.Release? _availableUpdate;

    public bool HasUpdate => AvailableUpdate != null;
    public string UpdateText => AvailableUpdate == null ? "" : L.F("Версия {0}", AvailableUpdate.Version);

    /// <summary>Проверка при запуске. Только если пользователь её включил: без спроса в сеть не ходим.</summary>
    public async Task CheckUpdatesOnStartAsync()
    {
        // Один раз спрашиваем разрешения: без него программа в сеть не ходит.
        if (!Settings.UpdatesAsked && Dialogs != null)
        {
            Settings.UpdatesAsked = true;
            if (!Settings.CheckUpdates)
                Settings.CheckUpdates = await Dialogs.ConfirmAsync(L.T("Проверять обновления?"),
                    L.T("HaPlayer будет заглядывать на GitHub и сообщать о новой версии. Больше никуда не обращается."),
                    L.T("Проверять"));
            Settings.Save();
        }
        if (!Settings.CheckUpdates) return;
        await CheckAndNotifyAsync();

        // Программу часто не закрывают сутками: проверяем ещё раз каждые 6 часов.
        _updateTimer ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromHours(6),
            Avalonia.Threading.DispatcherPriority.Background, async (_, _) =>
            {
                if (Settings.CheckUpdates) await CheckAndNotifyAsync();
            });
        _updateTimer.Start();
    }

    private Avalonia.Threading.DispatcherTimer? _updateTimer;
    private string? _notifiedVersion;

    /// <summary>Окно показывает уведомление: заголовок, текст и действие по щелчку. Задаёт MainWindow.</summary>
    public Action<string, string, Action>? Notify { get; set; }

    private async Task CheckAndNotifyAsync()
    {
        AvailableUpdate = await UpdateService.CheckAsync();
        if (AvailableUpdate is not { } r || r.Version == _notifiedVersion) return;
        _notifiedVersion = r.Version;
        Notify?.Invoke(L.F("Доступна версия {0}", r.Version), L.T("Нажмите, чтобы обновить"),
            () => _ = InstallUpdate());
    }

    // ---------- Мини-плеер ----------

    [ObservableProperty] private bool _isMiniPlayer;

    /// <summary>App показывает мини-плеер и прячет большое окно, или наоборот.</summary>
    public Action<bool>? MiniPlayerChanged { get; set; }

    partial void OnIsMiniPlayerChanged(bool value) => MiniPlayerChanged?.Invoke(value);

    [RelayCommand]
    private void ToggleMiniPlayer() => IsMiniPlayer = !IsMiniPlayer;

    // ---------- Глобальные горячие клавиши ----------

    private HaPlayer.Ui.GlobalHotkeys? _hotkeys;

    public bool GlobalHotkeysActive => _hotkeys != null;

    /// <summary>Включает или выключает клавиши, которые работают при свёрнутом окне.</summary>
    public void SetGlobalHotkeys(bool on)
    {
        _hotkeys?.Dispose();
        _hotkeys = on ? new HaPlayer.Ui.GlobalHotkeys(OnGlobalHotkey) : null;
        OnPropertyChanged(nameof(GlobalHotkeysActive));
    }

    private void OnGlobalHotkey(HaPlayer.Ui.GlobalHotkeys.Key key)
    {
        switch (key)
        {
            case HaPlayer.Ui.GlobalHotkeys.Key.PlayPause: Player.PlayPause(); break;
            case HaPlayer.Ui.GlobalHotkeys.Key.Next: Player.Next(); break;
            case HaPlayer.Ui.GlobalHotkeys.Key.Previous: Player.Previous(); break;
            case HaPlayer.Ui.GlobalHotkeys.Key.Stop: Player.Stop(); break;
            case HaPlayer.Ui.GlobalHotkeys.Key.VolumeUp: Player.ChangeVolume(5); break;
            case HaPlayer.Ui.GlobalHotkeys.Key.VolumeDown: Player.ChangeVolume(-5); break;
        }
    }

    public void DisposeHotkeys() => _hotkeys?.Dispose();

    /// <summary>Настоящий выход из программы (минуя сворачивание в трей). Задаёт App.</summary>
    public Action? ExitRequested { get; set; }

    /// <summary>
    /// Кнопка «Версия X.Y»: скачать новый HaPlayer.exe с GitHub, проверить и перезапуститься в новой версии.
    /// Если обновиться на месте нельзя, открывается страница релиза.
    /// </summary>
    [RelayCommand]
    public async Task InstallUpdate()
    {
        if (AvailableUpdate is not { } r || Dialogs == null || IsBusy) return;

        if (!UpdateService.CanSelfUpdate(r))
        {
            var open = await Dialogs.ConfirmAsync(L.F("Доступна версия {0}", r.Version),
                L.T("Обновить автоматически не получится: нет записи в папку программы. Открыть страницу релиза?"), L.T("Открыть"));
            if (open) OpenUrl(r.Url);
            return;
        }

        var mb = r.Size > 0 ? L.F(" Скачается {0:0} МБ.", r.Size / 1048576.0) : "";
        var ok = await Dialogs.ConfirmAsync(L.F("Обновить до {0}?", r.Version),
            L.F("HaPlayer скачает новую версию с GitHub и перезапустится.{0} Медиатека и настройки останутся.", mb), "Обновить");
        if (!ok) return;

        IsBusy = true;
        BusyText = L.T("Скачиваю обновление…");
        try
        {
            var progress = new Progress<double>(p => BusyText = L.F("Скачиваю обновление: {0:0}%", p));
            var file = await UpdateService.DownloadAsync(r, progress);
            BusyText = L.T("Устанавливаю…");
            SaveSession();
            Settings.Save();
            UpdateService.ApplyAndRestart(file, () => ExitRequested?.Invoke());
        }
        catch (Exception e)
        {
            IsBusy = false;
            var open = await Dialogs.ConfirmAsync(L.T("Обновить не получилось"), L.F("{0}\n\nОткрыть страницу релиза и скачать вручную?", e.Message), "Открыть");
            if (open) OpenUrl(r.Url);
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // браузер не открылся
        }
    }

    /// <summary>
    /// Переключает место хранения данных. Медиатека, плейлисты, лайки, фото и настройки копируются в новую папку,
    /// старая копия остаётся на месте. После этого HaPlayer перезапускается.
    /// </summary>
    public async Task ChangeDataLocationAsync(DataMode mode)
    {
        if (Dialogs == null) return;

        string? customRoot = null;
        if (mode == DataMode.Custom)
        {
            customRoot = await Dialogs.PickFolderAsync();
            if (customRoot == null) return;
        }

        var current = AppPaths.DataDirectory;
        var target = AppPaths.DirectoryFor(mode, customRoot);
        if (mode == AppPaths.Mode && string.Equals(Path.GetFullPath(target).TrimEnd('\\'), Path.GetFullPath(current).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
        {
            await Dialogs.MessageAsync(L.T("Данные уже здесь"), target);
            return;
        }

        try
        {
            Directory.CreateDirectory(target);
            var probe = Path.Combine(target, ".haplayer-write-test");
            await File.WriteAllTextAsync(probe, "");
            File.Delete(probe);
            if (mode == DataMode.Portable) AppPaths.SetMode(DataMode.Portable);
        }
        catch
        {
            await Dialogs.MessageAsync(L.T("Нет доступа к папке"),
                L.F("В папку {0} нельзя записывать.\n\nЕсли программа лежит в Program Files, для портативного режима переместите её, например, на рабочий стол или флешку.", target));
            return;
        }

        var targetDb = Path.Combine(target, "library.db");
        if (File.Exists(targetDb))
        {
            var useExisting = await Dialogs.ConfirmAsync(L.T("Здесь уже есть данные HaPlayer"),
                L.F("{0}\n\nОткрыть их? Текущая медиатека не перенесётся и останется на старом месте.", target), "Открыть их");
            if (!useExisting)
            {
                if (mode == DataMode.Portable && AppPaths.Mode != DataMode.Portable) File.Delete(AppPaths.PortableMarker);
                return;
            }
        }
        else
        {
            var ok = await Dialogs.ConfirmAsync(L.T("Перенести данные?"),
                L.F("Медиатека, плейлисты, любимые треки, фото и настройки будут скопированы сюда:\n{0}\n\nСтарая копия останется на месте. HaPlayer перезапустится.", target),
                L.T("Перенести"));
            if (!ok)
            {
                if (mode == DataMode.Portable && AppPaths.Mode != DataMode.Portable) File.Delete(AppPaths.PortableMarker);
                return;
            }

            SaveSession();
            IsBusy = true;
            BusyText = L.T("Переношу данные…");
            try
            {
                var oldImages = Path.Combine(current, "images");
                var newImages = Path.Combine(target, "images");
                await Task.Run(() =>
                {
                    _db.CopyTo(targetDb, oldImages, newImages);
                    Settings.SaveCopy(Path.Combine(target, "settings.json"));
                    if (Directory.Exists(oldImages))
                    {
                        Directory.CreateDirectory(newImages);
                        foreach (var f in Directory.EnumerateFiles(oldImages))
                            File.Copy(f, Path.Combine(newImages, Path.GetFileName(f)), overwrite: true);
                    }
                });
            }
            catch (Exception e)
            {
                IsBusy = false;
                if (mode == DataMode.Portable && AppPaths.Mode != DataMode.Portable) File.Delete(AppPaths.PortableMarker);
                await Dialogs.MessageAsync(L.T("Не получилось перенести данные"), e.Message);
                return;
            }
            IsBusy = false;
        }

        AppPaths.SetMode(mode, mode == DataMode.Custom ? target : null);
        RestartRequested?.Invoke();
    }

    // ---------- Сессия: что открыто и что играло ----------

    /// <summary>Запоминает открытую страницу, вкладку, очередь и позицию трека. Вызывается при закрытии окна.</summary>
    public void SaveSession()
    {
        Settings.LastPage = CurrentPage switch
        {
            SearchPageViewModel => "search",
            TracksPageViewModel => "tracks",
            SettingsPageViewModel => "settings",
            StatsPageViewModel => "stats",
            YearsPageViewModel => "years",
            CollectionPageViewModel c => $"{c.Item.Kind}:{c.Item.Key}",
            _ => "home",
        };
        Settings.LastTab = Tab.ToString();
        Settings.LastQueue = Player.Queue.Select(t => t.Path).ToList();
        Settings.LastIndex = Player.Index;
        Settings.LastPosition = Player.CurrentTrack != null ? Player.CurrentPosition : 0;
        Settings.LastQueueTitle = Player.QueueTitle;
        Settings.Save();
    }

    /// <summary>Возвращает то, что было открыто при закрытии. Трек ставится на паузу на той же секунде.</summary>
    public void RestoreSession()
    {
        if (Enum.TryParse<LibraryTab>(Settings.LastTab, out var tab)) Tab = tab;

        var page = Settings.LastPage ?? "home";
        var sep = page.IndexOf(':');
        if (sep > 0 && Find(page[..sep], page[(sep + 1)..]) is { } opened)
        {
            // Через Open: вкладка в боковой панели переключится на ту, где лежит открытая страница.
            Open(opened);
        }
        else
        {
            CurrentPage = page switch
            {
                "search" => new SearchPageViewModel(this),
                "tracks" => new TracksPageViewModel(this),
                "settings" => new SettingsPageViewModel(this),
                "stats" => new StatsPageViewModel(this),
                "years" => new YearsPageViewModel(this),
                _ => new HomePageViewModel(this),
            };
        }
        // Стартовая главная, с которой ушли при восстановлении, в историю «Назад» не нужна.
        _back.Clear();
        OnPropertyChanged(nameof(CanGoBack));

        // Очередь собираем заново из путей. Файлы, которых больше нет в медиатеке, пропускаем.
        // Если пользователь отключил восстановление, плеер стартует с пустой очередью.
        if (!Settings.RestoreQueue) return;
        var saved = Settings.LastQueue;
        if (saved.Count == 0 || Settings.LastIndex < 0 || Settings.LastIndex >= saved.Count) return;
        var current = saved[Settings.LastIndex];
        var queue = saved.Select(p => _byPath.GetValueOrDefault(p)).OfType<TrackItem>().ToList();
        var index = queue.FindIndex(t => string.Equals(t.Path, current, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;

        // Если очередь совпадает со списком на открытой странице, берём тот же список:
        // тогда кнопка «играть» на странице поставит паузу и не начнёт трек заново.
        IReadOnlyList<TrackItem> list = queue;
        if (CurrentPage is CollectionPageViewModel c && c.Item.Tracks.SequenceEqual(queue)) list = c.Item.Tracks;
        else if (Library.SequenceEqual(queue)) list = Library;

        Player.Restore(list, index, Settings.LastPosition, Settings.LastQueueTitle);
    }

    public void ApplyTheme()
    {
        HaPlayer.Ui.ThemeService.Apply(Settings.Palette, Settings.Surface, Settings.CustomAccent);
        Rebuild();
        if (CurrentPage is not SettingsPageViewModel) RefreshPage();
        NowPlayingBrush = Gradients.NowPlaying(HaPlayer.Ui.ThemeService.HeaderTint);
        _ = UpdateNowPlayingAsync();
    }

    /// <summary>Строки таблицы для списка треков. Общая фабрика, чтобы у всех страниц одинаково работали кнопки строки.</summary>
    public List<TrackRow> MakeRows(IReadOnlyList<TrackItem> list, string? queueTitle, Func<TrackItem, int, string> number,
        Func<TrackItem, string> secondary, bool showCover, Func<TrackItem, bool>? pinned = null)
    {
        var options = new TrackRowOptions(Settings.CompactList || Settings.Dense, Settings.ShowTechInfo, Settings.ShowPlayCount, Settings.ShowRatings);
        return list.Select((t, i) => new TrackRow(i, number(t, i), t, secondary(t), showCover,
            play: () => Player.PlayList(list, i, queueTitle),
            toggle: () =>
            {
                if (t.IsCurrent) Player.PlayPause();
                else Player.PlayList(list, i, queueTitle);
            },
            like: () => ToggleLike([t]), options, pinned?.Invoke(t) ?? false)).ToList();
    }

    /// <summary>Формат, частота и битрейт видны и в «Сейчас играет», если включены в настройках.</summary>
    public bool ShowTechInfo => Settings.ShowTechInfo;

    /// <summary>Настройки вида списка поменялись: пересобираем открытую страницу.</summary>
    public void ApplyListOptions()
    {
        OnPropertyChanged(nameof(ShowTechInfo));
        if (CurrentPage is not SettingsPageViewModel) RefreshPage();
        if (Settings.ShowTechInfo) _ = FillTechInfoAsync();
    }

    /// <summary>Строгий режим включили или выключили: альбомы и исполнители собираются заново.</summary>
    public void ApplyStrictTags()
    {
        Track.TagsOnly = Settings.StrictTags;
        foreach (var t in _library) t.Refresh();
        Rebuild();
        if (CurrentPage is not SettingsPageViewModel) RefreshPage();
    }

    /// <summary>«Все треки» с быстрым фильтром. Смена фильтра не попадает в историю «Назад».</summary>
    public void ShowTracks(string filter)
    {
        _replacingPage = CurrentPage is TracksPageViewModel;
        CurrentPage = new TracksPageViewModel(this, filter);
        _replacingPage = false;
    }

    // ---------- Оценки ----------

    public void SetRating(IReadOnlyList<TrackItem> tracks, int stars)
    {
        _db.SetRating(tracks.Select(t => t.Path), stars);
        foreach (var t in tracks) t.Rating = stars;
        if (CurrentPage is not SettingsPageViewModel) RefreshPage();
    }

    // ---------- Радио по треку ----------

    /// <summary>
    /// Очередь из похожих треков медиатеки: тот же исполнитель, жанр, близкий год, соседняя папка.
    /// Сначала играет сам трек, потом до 50 похожих в перемешанном порядке внутри одинаковой похожести.
    /// </summary>
    public void PlayRadio(TrackItem seed)
    {
        var m = seed.Model;
        var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(seed.Path) ?? "") ?? "";
        int Score(TrackItem t)
        {
            var s = 0;
            if (string.Equals(t.Artist, seed.Artist, StringComparison.OrdinalIgnoreCase)) s += 3;
            if (m.Genre.Length > 0 && string.Equals(t.Model.Genre, m.Genre, StringComparison.OrdinalIgnoreCase)) s += 3;
            if (m.Year > 0 && t.Model.Year > 0 && Math.Abs(t.Model.Year - m.Year) <= 3) s += 1;
            if (folder.Length > 0 && t.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) s += 1;
            if (t.IsLiked) s += 1;
            return s;
        }
        var similar = _library
            .Where(t => t != seed && !t.IsMissing && !t.IsBlacklisted && t.SongKey != seed.SongKey)
            .Select(t => (Track: t, Score: Score(t), Shuffle: Random.Shared.Next()))
            .Where(x => x.Score >= 2)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Shuffle)
            .Take(50)
            .Select(x => x.Track)
            .ToList();
        var list = new List<TrackItem> { seed };
        list.AddRange(similar);
        Player.Shuffle = false;
        Player.PlayList(list, 0, L.F("Радио: {0}", seed.Title));
    }

    // ---------- Пропавшие файлы ----------

    /// <summary>Убирает из медиатеки треки, файлов которых нет на месте. Файлы и так пропали, на диске ничего не меняется.</summary>
    public async Task RemoveMissingAsync()
    {
        if (Dialogs == null) return;
        var missing = _library.Where(t => t.IsMissing).ToList();
        if (missing.Count == 0) return;
        var ok = await Dialogs.ConfirmAsync(L.T("Убрать пропавшие треки?"),
            L.F("{0} без файла исчезнут из медиатеки, плейлистов и истории. Если диск просто отключён, подключите его и проверьте папки ещё раз.",
                Format.Tracks(missing.Count)),
            L.T("Убрать"));
        if (!ok) return;
        _db.Hide(missing.Select(t => t.Path).ToList());
        foreach (var t in missing)
        {
            _library.Remove(t);
            _byPath.Remove(t.Path);
            _hidden.Add(t.Path);
        }
        Rebuild();
        OnPropertyChanged(nameof(MissingCount));
        RefreshPage();
    }

    // ---------- Таймер сна ----------

    private Avalonia.Threading.DispatcherTimer? _sleepTimer;
    private DateTime _sleepAt;
    private const double SleepFadeSeconds = 30;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SleepTip))]
    private bool _isSleepActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SleepTip))]
    private string _sleepLeft = "";

    public string SleepTip => IsSleepActive ? L.F("Таймер сна: {0}", SleepLeft) : L.T("Таймер сна");

    /// <summary>Минуты до остановки. -1 — после этого трека, 0 — выключить.</summary>
    public void SetSleepTimer(int minutes)
    {
        _sleepTimer?.Stop();
        Player.StopAfterCurrent = false;
        Player.SetFade(1);
        IsSleepActive = minutes != 0;
        if (minutes == 0)
        {
            SleepLeft = "";
            return;
        }
        if (minutes < 0)
        {
            Player.StopAfterCurrent = true;
            SleepLeft = L.T("после этого трека");
            return;
        }
        _sleepAt = DateTime.Now.AddMinutes(minutes);
        _sleepTimer ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromSeconds(1),
            Avalonia.Threading.DispatcherPriority.Background, (_, _) => SleepTick());
        _sleepTimer.Start();
        SleepTick();
    }

    private void SleepTick()
    {
        var left = _sleepAt - DateTime.Now;
        if (left <= TimeSpan.Zero)
        {
            _sleepTimer?.Stop();
            Player.Pause();
            Player.SetFade(1);
            IsSleepActive = false;
            SleepLeft = "";
            return;
        }
        SleepLeft = left.TotalMinutes >= 1 ? L.F("{0} мин", (int)Math.Ceiling(left.TotalMinutes)) : L.F("{0} с", (int)left.TotalSeconds);
        // Последние 30 секунд музыка плавно затихает.
        Player.SetFade(left.TotalSeconds < SleepFadeSeconds ? left.TotalSeconds / SleepFadeSeconds : 1);
    }

    // ---------- Палитра команд ----------

    public PaletteViewModel Palette => _palette ??= new PaletteViewModel(this);
    private PaletteViewModel? _palette;

    // ---------- «Сейчас играет»: погружение, визуализатор, текст ----------

    /// <summary>Погружение: только большая обложка и управление, всё остальное прячется.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoverSize), nameof(CoverMaxWidth))]
    private bool _isImmersive;

    [ObservableProperty] private bool _isLyricsOpen;

    public double CoverSize => IsImmersive ? 760 : 520;
    public double CoverMaxWidth => IsImmersive ? 800 : 560;

    [RelayCommand]
    private void ToggleImmersive() => IsImmersive = !IsImmersive;

    [RelayCommand]
    private void ToggleLyrics() => IsLyricsOpen = !IsLyricsOpen;

    public bool ShowVisualizer => Settings.ShowVisualizer;

    public void NotifyVisualizer() => OnPropertyChanged(nameof(ShowVisualizer));

    private Lyrics? _lyrics;
    private int _lyricIndex = int.MinValue;

    [ObservableProperty] private List<LyricRow> _lyricWindow = [];
    [ObservableProperty] private string _lyricsPlain = "";

    public bool HasLyrics => _lyrics != null;
    public bool HasSyncedLyrics => _lyrics?.Synced == true;
    public bool HasPlainLyrics => _lyrics is { Synced: false };

    private async Task LoadLyricsAsync()
    {
        var track = Player.CurrentTrack;
        var lyrics = track == null ? null : await Task.Run(() => Lyrics.Load(track.Path));
        if (Player.CurrentTrack != track) return;
        _lyrics = lyrics;
        _lyricIndex = int.MinValue;
        LyricsPlain = lyrics is { Synced: false } ? string.Join("\n", lyrics.Lines.Select(l => l.Text)) : "";
        OnPropertyChanged(nameof(HasLyrics));
        OnPropertyChanged(nameof(HasSyncedLyrics));
        OnPropertyChanged(nameof(HasPlainLyrics));
        UpdateLyricLine();
    }

    /// <summary>Семь строк вокруг текущей: две до, текущая, четыре после. Чем дальше от текущей, тем бледнее.</summary>
    private void UpdateLyricLine()
    {
        if (_lyrics is not { Synced: true } lyrics)
        {
            if (LyricWindow.Count > 0) LyricWindow = [];
            return;
        }
        var index = lyrics.IndexAt(TimeSpan.FromSeconds(Player.Position));
        if (index == _lyricIndex) return;
        _lyricIndex = index;
        var rows = new List<LyricRow>();
        var center = Math.Max(index, 0);
        for (var i = center - 2; i <= center + 4; i++)
        {
            if (i < 0 || i >= lyrics.Lines.Count) continue;
            var text = lyrics.Lines[i].Text.Length > 0 ? lyrics.Lines[i].Text : "♪";
            var distance = Math.Abs(i - index);
            rows.Add(new LyricRow(text, i == index, i == index ? 1 : Math.Max(0.25, 0.7 - distance * 0.12)));
        }
        LyricWindow = rows;
    }

    // ---------- Сведения о файле ----------

    /// <summary>Окно со сведениями о файле трека. Файл только читается.</summary>
    public async Task ShowFileInfoAsync(TrackItem t)
    {
        if (Dialogs == null) return;
        var m = t.Model;
        if (m.SampleRate == 0 && File.Exists(t.Path) && await Task.Run(() => TagReader.ReadTechOnly(m)))
        {
            _db.UpdateTech([m]);
            t.Refresh();
        }

        long size = 0;
        try { size = new FileInfo(t.Path).Length; } catch { /* файла нет: размер не показываем */ }

        var lines = new List<string>
        {
            t.Path,
            "",
            L.F("Формат: {0}", t.TechText),
            L.F("Длительность: {0}", t.DurationText),
        };
        if (size > 0) lines.Add(L.F("Размер: {0:0.0} МБ", size / 1048576.0));
        if (m.Year > 0) lines.Add(L.F("Год: {0}", m.Year));
        if (!string.IsNullOrEmpty(t.AddedText)) lines.Add(L.F("Добавлен: {0}", t.AddedText));
        if (t.PlayCount > 0) lines.Add(L.F("Прослушан: {0}", L.Plural(t.PlayCount, "раз", "раза", "раз", "time", "times")));
        if (t.Rating > 0) lines.Add(L.F("Оценка: {0}", t.Stars));
        if (Note("track", t.Path) is { Length: > 0 } note) lines.Add(L.F("Заметка: {0}", note));
        if (t.IsBlacklisted) lines.Add(L.T("Не попадает в подборки и перемешивание"));
        if (!string.IsNullOrEmpty(m.Genre)) lines.Add(L.F("Жанр: {0}", m.Genre));
        var inPlaylists = Playlists.Where(p => p.Tracks.Contains(t)).Select(p => p.Name).ToList();
        if (inPlaylists.Count > 0) lines.Add(L.F("В плейлистах: {0}", string.Join(", ", inPlaylists)));
        if (t.IsMissing) lines.Add(L.T("Файл не найден"));
        await Dialogs.MessageAsync(t.Title, string.Join("\n", lines));
    }

    // ---------- Подборки ----------

    private const int SmartLimit = 100;

    /// <summary>Подборки собираются заново при каждом обращении: они дешёвые и всегда свежие.</summary>
    public List<SmartItem> SmartPlaylists
    {
        get
        {
            var monthAgo = DateTime.UtcNow.AddDays(-30).Ticks;
            var quarterAgo = DateTime.UtcNow.AddDays(-91).Ticks;
            var pool = _library.Where(t => !IsHiddenTrack(t)).ToList();
            var recent = pool.Where(t => t.Model.AddedTicks >= monthAgo).OrderByDescending(t => t.Model.AddedTicks).ToList();
            var recentText = L.T("Добавлено за последний месяц");
            if (recent.Count == 0)
            {
                recent = pool.OrderByDescending(t => t.Model.AddedTicks).Take(SmartLimit).ToList();
                recentText = L.T("Последние 100 треков");
            }
            var lossless = pool.Where(t => t.Model.IsLossless).ToList();
            var rated = pool.Where(t => t.Rating >= 4).OrderByDescending(t => t.Rating).ToList();
            var top = pool.Where(t => t.PlayCount > 0)
                .OrderByDescending(t => t.PlayCount).ThenByDescending(t => t.LastPlayedTicks).Take(SmartLimit).ToList();
            var forgotten = pool.Where(t => t.PlayCount > 0 && t.LastPlayedTicks < quarterAgo)
                .OrderByDescending(t => t.PlayCount).Take(SmartLimit).ToList();
            return
            [
                new SmartItem("recent", L.T("Новое в медиатеке"), recentText, recent, Covers, Open, Play),
                new SmartItem("top", L.T("Часто слушаю"), L.T("По истории прослушивания"), top, Covers, Open, Play),
                new SmartItem("forgotten", L.T("Давно не слушал"), L.T("Не звучало больше трёх месяцев"), forgotten, Covers, Open, Play),
                new SmartItem("lossless", L.T("Без потерь"), L.T("FLAC, WAV, ALAC"), lossless, Covers, Open, Play),
                .. Settings.ShowRatings
                    ? new[] { new SmartItem("rated", L.T("Высокий рейтинг"), L.T("Оценка 4 и 5"), rated, Covers, Open, Play) }
                    : [],
            ];
        }
    }

    // ---------- Экспорт плейлиста ----------

    /// <summary>Сохраняет список треков в .m3u8 (UTF-8, полные пути): его откроют Winamp, AIMP, foobar2000 и VLC.</summary>
    public async Task ExportM3uAsync(CollectionItem item)
    {
        if (Dialogs == null || item.Tracks.Count == 0) return;
        var file = await Dialogs.PickSaveFileAsync(item.Name, ".m3u8");
        if (file == null) return;
        try
        {
            var sb = new System.Text.StringBuilder("#EXTM3U\n");
            foreach (var t in item.Tracks)
            {
                sb.Append("#EXTINF:").Append((int)Math.Round(t.Model.DurationSeconds)).Append(',')
                  .Append(t.Artist).Append(" - ").Append(t.Title).Append('\n');
                sb.Append(t.Path).Append('\n');
            }
            await File.WriteAllTextAsync(file, sb.ToString(), new System.Text.UTF8Encoding(false));
        }
        catch (Exception e)
        {
            await Dialogs.MessageAsync(L.T("Не получилось сохранить"), e.Message);
        }
    }

    // ---------- Проверка файлов ----------

    private bool _checkingFiles;

    /// <summary>
    /// Отмечает треки, файлов которых нет на месте (отключён диск, файл удалён или переименован),
    /// и дочитывает техданные, если они включены. Идёт в фоне после показа окна и после проверки папок.
    /// </summary>
    public async Task CheckFilesAsync()
    {
        if (_checkingFiles) return;
        _checkingFiles = true;
        try
        {
            var paths = _library.Select(t => t.Path).ToList();
            var missing = await Task.Run(() => paths.Where(p => !File.Exists(p)).ToHashSet(StringComparer.OrdinalIgnoreCase));
            foreach (var t in _library) t.IsMissing = missing.Contains(t.Path);
            OnPropertyChanged(nameof(MissingCount));
        }
        finally
        {
            _checkingFiles = false;
        }
        // Техданные и жанр дочитываются всегда: жанр нужен «Радио по треку».
        await FillTechInfoAsync();
    }

    public int MissingCount => _library.Count(t => t.IsMissing);

    private bool _fillingTech;

    /// <summary>Битрейт, частота и каналы для треков, добавленных до 1.6. Файлы только читаются.</summary>
    private async Task FillTechInfoAsync()
    {
        if (_fillingTech) return;
        // До 1.8 жанр не запоминался: один раз перечитываем все треки, дальше только новые.
        var allOnce = !Settings.GenresRead;
        var todo = _library.Where(t => (allOnce || t.Model.SampleRate == 0) && !t.IsMissing).Select(t => t.Model).ToList();
        if (todo.Count == 0)
        {
            Settings.GenresRead = true;
            return;
        }
        _fillingTech = true;
        try
        {
            var done = await Task.Run(() =>
            {
                var ok = new List<Track>();
                foreach (var m in todo)
                    if (TagReader.ReadTechOnly(m)) ok.Add(m);
                _db.UpdateTech(ok);
                return ok.Count;
            });
            if (allOnce)
            {
                Settings.GenresRead = true;
                Settings.Save();
            }
            if (done > 0) Player.CurrentTrack?.Refresh();
            if (done > 0 && CurrentPage is not SettingsPageViewModel) RefreshPage();
        }
        finally
        {
            _fillingTech = false;
        }
    }

    /// <summary>Все треки в порядке добавления.</summary>
    public IReadOnlyList<TrackItem> Library => _library;

    public ObservableCollection<AlbumItem> Albums { get; } = [];
    public ObservableCollection<ArtistItem> Artists { get; } = [];
    public ObservableCollection<PlaylistItem> Playlists { get; } = [];

    /// <summary>«Любимые треки» и плейлисты пользователя: для поиска и меню.</summary>
    public IEnumerable<CollectionItem> AllPlaylists => Playlists.Prepend<CollectionItem>(_liked);

    private LikedItem _liked;
    public LikedItem Liked => _liked;

    private AllTracksItem _allTracks;

    /// <summary>Альбомы от последних добавленных к первым, для главной.</summary>
    public List<AlbumItem> RecentAlbums { get; private set; } = [];

    public bool HasNoAlbums => Albums.Count == 0;
    public bool HasNoArtists => Artists.Count == 0;

    // ---------- Медиатека слева: один список, фильтры-чипы, поиск, сортировка ----------

    /// <summary>То, что сейчас видно в медиатеке слева.</summary>
    public ObservableCollection<CollectionItem> LibraryItems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlaylistsTab), nameof(IsAlbumsTab), nameof(IsArtistsTab), nameof(IsFoldersTab), nameof(HasFilter))]
    private LibraryTab _tab = LibraryTab.All;

    public bool IsPlaylistsTab => Tab == LibraryTab.Playlists;
    public bool IsAlbumsTab => Tab == LibraryTab.Albums;
    public bool IsArtistsTab => Tab == LibraryTab.Artists;
    public bool IsFoldersTab => Tab == LibraryTab.Folders;
    public bool HasFilter => Tab != LibraryTab.All;

    /// <summary>Чип включает фильтр, повторное нажатие снимает его, как в Spotify.</summary>
    [RelayCommand]
    private void SelectTab(string tab)
    {
        var t = Enum.Parse<LibraryTab>(tab);
        Tab = Tab == t ? LibraryTab.All : t;
    }

    partial void OnTabChanged(LibraryTab value) => RefreshLibraryList();

    [ObservableProperty] private bool _isLibrarySearchOpen;
    [ObservableProperty] private string _libraryQuery = "";

    partial void OnLibraryQueryChanged(string value) => RefreshLibraryList();

    [RelayCommand]
    private void ToggleLibrarySearch()
    {
        IsLibrarySearchOpen = !IsLibrarySearchOpen;
        if (!IsLibrarySearchOpen) LibraryQuery = "";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LibrarySortText))]
    private bool _sortAlphabetical;

    public string LibrarySortText => SortAlphabetical ? L.T("По алфавиту") : L.T("Недавние");

    partial void OnSortAlphabeticalChanged(bool value)
    {
        Settings.LibrarySortAlpha = value;
        RefreshLibraryList();
    }

    [RelayCommand]
    private void ToggleLibrarySort() => SortAlphabetical = !SortAlphabetical;

    [ObservableProperty] private CollectionItem? _selectedLibraryItem;

    partial void OnSelectedLibraryItemChanged(CollectionItem? value)
    {
        if (!_syncingSelection && value != null) Open(value);
    }

    private static string RecentKey(CollectionItem item) => $"{item.Kind}:{item.Key}";

    /// <summary>Запоминает, что коллекцию открывали или включали: «Недавние» сортируются по этому.</summary>
    private void Touch(CollectionItem item)
    {
        var key = RecentKey(item);
        Settings.RecentCollections.Remove(key);
        Settings.RecentCollections.Insert(0, key);
        if (Settings.RecentCollections.Count > 300) Settings.RecentCollections.RemoveRange(300, Settings.RecentCollections.Count - 300);
    }

    private void RefreshLibraryList()
    {
        IEnumerable<CollectionItem> items = Tab switch
        {
            LibraryTab.Playlists => Playlists,
            LibraryTab.Albums => RecentAlbums,
            LibraryTab.Artists => Artists,
            LibraryTab.Folders => RootFolders(),
            _ => Playlists.Cast<CollectionItem>().Concat(RecentAlbums).Concat(Artists),
        };

        var q = (LibraryQuery ?? "").Trim().ToLowerInvariant();
        bool Match(CollectionItem i) => q.Length == 0 || i.Name.ToLowerInvariant().Contains(q) || i.Subtitle.ToLowerInvariant().Contains(q);
        items = items.Where(Match);

        if (SortAlphabetical)
        {
            items = items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase);
        }
        else
        {
            var rank = Settings.RecentCollections.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);
            items = items.Select((it, i) => (it, i))
                .OrderBy(x => rank.TryGetValue(RecentKey(x.it), out var r) ? r : int.MaxValue)
                .ThenBy(x => x.i)
                .Select(x => x.it);
        }

        // Закреплённые сверху: «Любимые треки», «Все треки», затем закреплённое пользователем в порядке закрепления.
        var pinned = new List<CollectionItem>();
        if (Tab is LibraryTab.All or LibraryTab.Playlists && Match(_liked)) pinned.Add(_liked);
        if (Tab == LibraryTab.All && Match(_allTracks)) pinned.Add(_allTracks);

        var pinOrder = Settings.PinnedCollections.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);
        var list = items.ToList();
        foreach (var it in list) it.IsUserPinned = pinOrder.ContainsKey(RecentKey(it));
        pinned.AddRange(list.Where(i => i.IsUserPinned).OrderBy(i => pinOrder[RecentKey(i)]));

        Replace(LibraryItems, pinned.Concat(list.Where(i => !i.IsUserPinned)));
        MarkNowPlaying();
        SyncLibrarySelection();
    }

    private void SyncLibrarySelection()
    {
        var current = CurrentPage switch
        {
            CollectionPageViewModel c => LibraryItems.FirstOrDefault(i => i.Kind == c.Item.Kind && i.Key == c.Item.Key),
            TracksPageViewModel => _allTracks,
            _ => null,
        };
        _syncingSelection = true;
        SelectedLibraryItem = current;
        _syncingSelection = false;
    }

    /// <summary>Подсвечивает в медиатеке коллекцию, из которой сейчас играет очередь.</summary>
    private void MarkNowPlaying()
    {
        var queue = Player.Queue;
        foreach (var item in LibraryItems)
            item.IsNowPlaying = queue.Count > 0 && (ReferenceEquals(item.Tracks, queue) ||
                                                    (item.Name == Player.QueueTitle && item.Tracks.Count == queue.Count));
    }

    // ---------- Страницы и история «Назад» / «Вперёд» ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeActive), nameof(IsStatsActive), nameof(IsTracksActive), nameof(IsSearchActive), nameof(IsSettingsActive), nameof(SettingsTip))]
    private object _currentPage;

    public bool IsHomeActive => CurrentPage is HomePageViewModel;
    public bool IsStatsActive => CurrentPage is StatsPageViewModel;
    public bool IsTracksActive => CurrentPage is TracksPageViewModel;
    public bool IsSearchActive => CurrentPage is SearchPageViewModel;

    private readonly List<object> _back = [];
    private readonly List<object> _forward = [];
    private bool _historyNavigation;
    private bool _replacingPage;

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    partial void OnCurrentPageChanged(object? oldValue, object newValue)
    {
        if (oldValue != null && !_historyNavigation && !_replacingPage)
        {
            _back.Add(oldValue);
            if (_back.Count > 50) _back.RemoveAt(0);
            _forward.Clear();
        }
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        SyncLibrarySelection();
        IsNowPlayingOpen = false;
        IsLibraryExpanded = false;
        Crumbs = BuildCrumbs();
    }

    // ---------- Хлебные крошки ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCrumbs))]
    private List<Crumb> _crumbs = [];

    public bool HasCrumbs => Crumbs.Count > 0;

    /// <summary>
    /// Где мы: «Главная › Исполнитель › Альбом». Строится по смыслу страницы, а не по истории переходов.
    /// На главной и страницах первого уровня с крупным заголовком (настройки, поиск, итоги) крошек нет.
    /// </summary>
    private List<Crumb> BuildCrumbs()
    {
        var home = new Crumb(L.T("Главная"), () => GoHomeCommand.Execute(null));
        switch (CurrentPage)
        {
            case TracksPageViewModel:
                return [home, new Crumb(L.T("Все треки"), null)];
            case CollectionPageViewModel { Item: AlbumItem album }:
            {
                var artist = Artists.FirstOrDefault(a => a.Key == ArtistItem.MakeKey(album.Artist));
                var list = new List<Crumb> { home };
                if (artist != null) list.Add(new Crumb(artist.Name, () => Open(artist)));
                list.Add(new Crumb(album.Name, null));
                return list;
            }
            case CollectionPageViewModel { Item: FolderItem folder }:
            {
                var list = new List<Crumb> { home };
                // Папки выше открытой, начиная с папки медиатеки.
                foreach (var parent in FolderParents(folder)) list.Add(new Crumb(parent.Name, () => Open(parent)));
                list.Add(new Crumb(folder.Name, null));
                return list;
            }
            case CollectionPageViewModel c:
                return [home, new Crumb(c.Item.Name, null)];
            case YearsPageViewModel:
                return [home, new Crumb(L.T("По годам"), null)];
            default:
                return [];
        }
    }

    // ---------- Папки ----------

    private static string Norm(string folder) => folder.TrimEnd('\\', '/');

    /// <summary>Папка со всем содержимым. Строится по запросу: папок много, а открывают единицы.</summary>
    public FolderItem Folder(string path)
    {
        path = Norm(path);
        var prefix = path + "\\";
        var all = _library.Where(t => t.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var direct = all.Where(t => string.Equals(Path.GetDirectoryName(t.Path), path, StringComparison.OrdinalIgnoreCase)).ToList();
        return new FolderItem(path, direct, all, Covers, Open, Play);
    }

    /// <summary>Вложенные папки первого уровня, в которых есть музыка.</summary>
    public List<FolderItem> SubFolders(FolderItem folder)
    {
        var prefix = folder.Path + "\\";
        return _library
            .Select(t => Path.GetDirectoryName(t.Path) ?? "")
            .Where(d => d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(d => prefix + d[prefix.Length..].Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.CurrentCultureIgnoreCase)
            .Select(Folder)
            .ToList();
    }

    /// <summary>Корни для вкладки «Папки»: папки медиатеки и папки отдельно добавленных файлов.</summary>
    private List<CollectionItem> RootFolders()
    {
        var roots = Settings.MusicFolders.Select(Norm).ToList();
        foreach (var t in _library)
        {
            var dir = Path.GetDirectoryName(t.Path) ?? "";
            if (!roots.Any(r => dir.Equals(r, StringComparison.OrdinalIgnoreCase) || dir.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)))
                roots.Add(dir);
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).Select(Folder).Where(f => f.AllTracks.Count > 0).Cast<CollectionItem>().ToList();
    }

    /// <summary>Папки выше открытой, от корня медиатеки. Для хлебных крошек.</summary>
    private List<FolderItem> FolderParents(FolderItem folder)
    {
        var roots = Settings.MusicFolders.Select(Norm).ToList();
        var root = roots.FirstOrDefault(r => folder.Path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase));
        if (root == null) return [];
        var list = new List<FolderItem>();
        var p = Path.GetDirectoryName(folder.Path);
        while (p != null && p.Length >= root.Length)
        {
            list.Insert(0, Folder(p));
            p = Path.GetDirectoryName(p);
        }
        return list;
    }

    // ---------- По годам ----------

    [RelayCommand]
    private void GoYears()
    {
        if (CurrentPage is not YearsPageViewModel) CurrentPage = new YearsPageViewModel(this);
    }

    // ---------- Объединение исполнителей, склеивание и разделение альбомов ----------
    // Всё через привязки HaPlayer: теги файлов не меняются, «Вернуть как в тегах» отменяет.

    public void MergeArtist(ArtistItem from, ArtistItem into) => AssignToArtist(into.Name, from.Tracks.ToList());

    public void GlueAlbum(AlbumItem from, AlbumItem into) => AssignToAlbum(into.Key, from.Tracks.ToList());

    /// <summary>Альбом из нескольких папок делится на отдельные альбомы, по одному на папку.</summary>
    public void SplitAlbumByFolders(AlbumItem album)
    {
        var groups = album.Tracks.GroupBy(t => Path.GetDirectoryName(t.Path) ?? "", StringComparer.OrdinalIgnoreCase).ToList();
        if (groups.Count < 2) return;
        foreach (var g in groups)
        {
            var name = $"{album.Name} ({Path.GetFileName(g.Key)})";
            var key = Track.MakeAlbumKey(name, album.Artist);
            var paths = g.Select(t => t.Path).ToList();
            _db.SetUserAlbum(paths, key);
            foreach (var t in g)
            {
                t.Model.UserAlbumKey = key;
                t.Refresh();
            }
        }
        Rebuild();
        RefreshPage();
    }

    /// <summary>Похожие имена первыми: регистр, пробелы, «the» и знаки не учитываются.</summary>
    public static string NameKey(string name) =>
        new string(name.ToLowerInvariant().Replace("the ", "").Where(char.IsLetterOrDigit).ToArray());

    // ---------- Скрыть с главной ----------

    private static string HideKey(CollectionItem item) => $"{item.Kind}:{item.Key}";

    public bool IsHiddenFromHome(CollectionItem item) => Settings.HiddenFromHome.Contains(HideKey(item));

    public void ToggleHiddenFromHome(CollectionItem item)
    {
        var key = HideKey(item);
        if (!Settings.HiddenFromHome.Remove(key)) Settings.HiddenFromHome.Add(key);
        Settings.Save();
        if (CurrentPage is HomePageViewModel) RefreshPage();
    }

    /// <summary>Трек из скрытого альбома или исполнителя не попадает в подборки.</summary>
    public bool IsHiddenTrack(TrackItem t) =>
        t.IsBlacklisted
        || (t.Album != null && Settings.HiddenFromHome.Contains("album:" + t.Album.Key))
        || Settings.HiddenFromHome.Contains("artist:" + ArtistItem.MakeKey(t.Artist));

    // ---------- Чёрный список ----------

    public void ToggleBlacklist(IReadOnlyList<TrackItem> tracks)
    {
        var add = tracks.Any(t => !t.IsBlacklisted);
        foreach (var t in tracks)
        {
            t.IsBlacklisted = add;
            Settings.Blacklist.RemoveAll(p => string.Equals(p, t.Path, StringComparison.OrdinalIgnoreCase));
            if (add) Settings.Blacklist.Add(t.Path);
        }
        Settings.Save();
        OnPropertyChanged(nameof(BlacklistCount));
    }

    public int BlacklistCount => _library.Count(t => t.IsBlacklisted);

    public void ClearBlacklist()
    {
        foreach (var t in _library) t.IsBlacklisted = false;
        Settings.Blacklist.Clear();
        Settings.Save();
        OnPropertyChanged(nameof(BlacklistCount));
    }

    // ---------- Закрепление треков в плейлисте ----------

    private List<TrackItem> PinnedFirst(long playlistId, List<TrackItem> tracks)
    {
        var prefix = playlistId + "|";
        var pins = Settings.PinnedTracks.Where(p => p.StartsWith(prefix)).Select(p => p[prefix.Length..]).ToList();
        if (pins.Count == 0) return tracks;
        var pinned = pins.Select(p => tracks.FirstOrDefault(t => string.Equals(t.Path, p, StringComparison.OrdinalIgnoreCase)))
            .OfType<TrackItem>().ToList();
        return pinned.Concat(tracks.Where(t => !pinned.Contains(t))).ToList();
    }

    public bool IsPinnedIn(PlaylistItem playlist, TrackItem t) =>
        Settings.PinnedTracks.Contains(playlist.Id + "|" + t.Path, StringComparer.OrdinalIgnoreCase);

    public void TogglePinInPlaylist(PlaylistItem playlist, IReadOnlyList<TrackItem> tracks)
    {
        var add = tracks.Any(t => !IsPinnedIn(playlist, t));
        foreach (var t in tracks)
        {
            var key = playlist.Id + "|" + t.Path;
            Settings.PinnedTracks.RemoveAll(p => string.Equals(p, key, StringComparison.OrdinalIgnoreCase));
            if (add) Settings.PinnedTracks.Add(key);
        }
        Settings.Save();
        RebuildPlaylists();
        RefreshPage();
    }

    // ---------- Заметки ----------

    private readonly Dictionary<(string Kind, string Key), string> _notes;

    public string Note(string kind, string key) => _notes.GetValueOrDefault((kind, key)) ?? "";

    public async Task EditNoteAsync(string kind, string key, string title)
    {
        if (Dialogs == null) return;
        var text = await Dialogs.PromptAsync(L.F("Заметка: {0}", title), L.T("Текст заметки"), Note(kind, key), L.T("Сохранить"));
        if (text == null) return;
        _db.SetNote(kind, key, text);
        if (string.IsNullOrWhiteSpace(text)) _notes.Remove((kind, key));
        else _notes[(kind, key)] = text.Trim();
        RefreshPage();
    }

    // ---------- «Показать в медиатеке» ----------

    /// <summary>Открывает альбом играющего трека, прокручивает к нему и выделяет строку.</summary>
    [RelayCommand]
    public void RevealCurrent()
    {
        if (Player.CurrentTrack is not { Album: { } album } track) return;
        IsNowPlayingOpen = false;
        if (CurrentPage is not CollectionPageViewModel c || c.Item != album) Open(album);
        if (CurrentPage is CollectionPageViewModel page) page.Reveal(track);
    }

    [RelayCommand]
    private void GoBack()
    {
        if (_back.Count == 0) return;
        var target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _forward.Add(CurrentPage);
        _historyNavigation = true;
        CurrentPage = target;
        _historyNavigation = false;
    }

    [RelayCommand]
    private void GoForward()
    {
        if (_forward.Count == 0) return;
        var target = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        _back.Add(CurrentPage);
        _historyNavigation = true;
        CurrentPage = target;
        _historyNavigation = false;
    }

    [RelayCommand]
    private void GoStats()
    {
        if (CurrentPage is StatsPageViewModel) GoBack();
        else CurrentPage = new StatsPageViewModel(this);
    }

    /// <summary>Все прослушивания из истории с треками, которые есть в медиатеке. Для «Итогов».</summary>
    public List<(TrackItem Track, DateTime At)> LoadAllPlays() =>
        _db.LoadHistory(1_000_000)
            .Select(h => (Track: _byPath.GetValueOrDefault(h.Path), At: new DateTime(h.PlayedAt, DateTimeKind.Utc).ToLocalTime()))
            .Where(x => x.Track != null)
            .Select(x => (x.Track!, x.At))
            .ToList();

    [RelayCommand]
    private void GoHome()
    {
        if (CurrentPage is not HomePageViewModel) CurrentPage = new HomePageViewModel(this);
    }

    [RelayCommand]
    private void GoTracks()
    {
        if (CurrentPage is not TracksPageViewModel) CurrentPage = new TracksPageViewModel(this);
    }

    // ---------- Поиск в верхней панели ----------

    /// <summary>Текст в поле поиска наверху. Ввод сразу открывает страницу результатов.</summary>
    [ObservableProperty] private string _searchQuery = "";

    /// <summary>Окно ставит фокус в поле поиска (Ctrl+F).</summary>
    public Action? FocusSearchRequested { get; set; }

    partial void OnSearchQueryChanged(string value)
    {
        if (CurrentPage is SearchPageViewModel s) s.SetQuery(value);
        else if (!string.IsNullOrWhiteSpace(value)) CurrentPage = new SearchPageViewModel(this);
    }

    [RelayCommand]
    public void GoSearch()
    {
        if (CurrentPage is not SearchPageViewModel) CurrentPage = new SearchPageViewModel(this);
        FocusSearchRequested?.Invoke();
    }

    // ---------- Правая панель: «Сейчас играет», «Устройства», «История» ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQueuePanelOpen), nameof(ShowQueuePanel), nameof(ShowDevicesPanel),
        nameof(ShowHistoryPanel), nameof(IsDevicesPanelOpen), nameof(IsHistoryPanelOpen))]
    private RightPanelMode _rightPanel;

    public bool IsQueuePanelOpen => RightPanel == RightPanelMode.NowPlaying;
    public bool IsDevicesPanelOpen => RightPanel == RightPanelMode.Devices;
    public bool IsHistoryPanelOpen => RightPanel == RightPanelMode.History;
    public bool ShowDevicesPanel => RightPanel == RightPanelMode.Devices;
    public bool ShowHistoryPanel => RightPanel == RightPanelMode.History;

    partial void OnRightPanelChanged(RightPanelMode value)
    {
        Settings.ShowQueuePanel = value == RightPanelMode.NowPlaying;
        if (value == RightPanelMode.Devices) RefreshDevices();
        if (value == RightPanelMode.History) LoadHistory();
    }

    /// <summary>Значок панели работает как переключатель: открыть, а повторное нажатие закрывает.</summary>
    private void TogglePanel(RightPanelMode mode) => RightPanel = RightPanel == mode ? RightPanelMode.None : mode;

    [RelayCommand]
    private void ToggleQueuePanel() => TogglePanel(RightPanelMode.NowPlaying);

    [RelayCommand]
    private void ToggleDevicesPanel() => TogglePanel(RightPanelMode.Devices);

    [RelayCommand]
    private void ToggleHistoryPanel() => TogglePanel(RightPanelMode.History);

    [RelayCommand]
    private void CloseRightPanel() => RightPanel = RightPanelMode.None;

    // ---------- Устройства вывода (как Spotify Connect, только для звука этого компьютера) ----------

    [ObservableProperty] private DeviceItem? _currentDevice;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOtherDevices))]
    private List<DeviceItem> _otherDevices = [];

    public bool HasOtherDevices => OtherDevices.Count > 0;

    [RelayCommand]
    private void RefreshDevices()
    {
        var all = Engine.GetDevices();
        // Текущее: выбранное пользователем, а если его нет или оно отключено — устройство по умолчанию.
        var current = all.FirstOrDefault(d => d.Id == Engine.DeviceId) ?? all.FirstOrDefault(d => d.IsDefault) ?? all.FirstOrDefault();
        CurrentDevice = current == null ? null : new DeviceItem(current, true, () => { });
        OtherDevices = all.Where(d => d != current).Select(d => new DeviceItem(d, false, () => SelectDevice(d.Id))).ToList();
    }

    /// <summary>Открывает параметры звука Windows: там подключают Bluetooth и выбирают устройства.</summary>
    [RelayCommand]
    private void OpenSoundSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
        }
        catch
        {
            // на старых сборках Windows ссылки ms-settings может не быть
        }
    }

    private void SelectDevice(string id)
    {
        Engine.SetDevice(id);
        Settings.OutputDeviceId = id;
        Settings.Save();
        RefreshDevices();
    }

    // ---------- История прослушивания ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHistory), nameof(HistoryEmpty))]
    private List<HistoryItem> _history = [];

    public bool HasHistory => History.Count > 0;
    public bool HistoryEmpty => History.Count == 0;

    public string HistoryEmptyTitle => Settings.KeepHistory
        ? L.T("Здесь появится то, что вы слушали")
        : L.T("История прослушивания выключена");

    public string HistoryEmptyText => Settings.KeepHistory
        ? L.T("Сюда попадает всё, что звучало дольше 30 секунд. Хранится только на этом компьютере.")
        : L.T("Включается в настройках, раздел «Данные».");

    private void LoadHistory()
    {
        var rows = _db.LoadHistory(200);
        var items = new List<HistoryItem>();
        foreach (var (path, ticks) in rows)
        {
            if (!_byPath.TryGetValue(path, out var t)) continue;
            var when = new DateTime(ticks, DateTimeKind.Utc).ToLocalTime();
            items.Add(new HistoryItem(t, when, () => Player.PlayList([t], 0, L.T("История прослушивания"))));
        }
        History = items;
        OnPropertyChanged(nameof(HistoryEmptyTitle));
        OnPropertyChanged(nameof(HistoryEmptyText));
    }

    /// <summary>Настройки сообщают, что включили или выключили историю.</summary>
    public void OnKeepHistoryChanged()
    {
        if (IsHistoryPanelOpen) LoadHistory();
    }

    private void OnListened(TrackItem track)
    {
        if (!Settings.KeepHistory) return;
        _db.AddHistory(track.Path);
        track.PlayCount++;
        track.LastPlayedTicks = DateTime.UtcNow.Ticks;
        if (IsHistoryPanelOpen) LoadHistory();
    }

    [RelayCommand]
    private async Task ClearHistory()
    {
        if (Dialogs == null || !HasHistory) return;
        var ok = await Dialogs.ConfirmAsync(L.T("Очистить историю?"),
            L.T("Список прослушанных треков удалится с этого компьютера. Сами треки останутся в медиатеке."), L.T("Очистить"));
        if (!ok) return;
        _db.ClearHistory();
        LoadHistory();
    }

    [RelayCommand]
    private void OpenHistorySettings()
    {
        CloseRightPanel();
        if (CurrentPage is not SettingsPageViewModel) CurrentPage = new SettingsPageViewModel(this);
    }

    // ---------- Закрепление и удаление (меню «…» и правый клик в медиатеке) ----------

    public void TogglePin(CollectionItem item)
    {
        if (item.IsBuiltInPinned) return;
        var key = RecentKey(item);
        if (!Settings.PinnedCollections.Remove(key)) Settings.PinnedCollections.Add(key);
        Settings.Save();
        RefreshLibraryList();
    }

    /// <summary>
    /// Плейлист удаляется из HaPlayer. Альбом и исполнитель убираются из медиатеки вместе с треками,
    /// но файлы на диске остаются: плеер ничего не удаляет у пользователя.
    /// </summary>
    public async Task DeleteCollectionAsync(CollectionItem item)
    {
        if (Dialogs == null) return;
        if (item is PlaylistItem p)
        {
            await DeletePlaylistAsync(p);
            Settings.PinnedCollections.Remove(RecentKey(item));
            return;
        }
        if (item is not (AlbumItem or ArtistItem)) return;

        var tracks = item.Tracks.ToList();
        var ok = await Dialogs.ConfirmAsync(L.F("Убрать {0} из медиатеки?", item.KindName),
            L.F("«{0}»: {1} исчезнут из HaPlayer, плейлистов и истории.", item.Name, Format.Tracks(tracks.Count)) + "\n\n" +
            L.T("Файлы на диске останутся на месте. При проверке папок они не вернутся. Чтобы вернуть, добавьте их снова через «Настройки → Добавить файлы»."),
            L.T("Убрать"));
        if (!ok) return;

        var paths = tracks.Select(t => t.Path).ToList();
        _db.Hide(paths);
        foreach (var t in tracks)
        {
            _library.Remove(t);
            _byPath.Remove(t.Path);
            _hidden.Add(t.Path);
        }
        Settings.PinnedCollections.Remove(RecentKey(item));
        Settings.Save();
        Rebuild();
        if (CurrentPage is CollectionPageViewModel c && c.Item.Kind == item.Kind && c.Item.Key == item.Key)
            CurrentPage = new HomePageViewModel(this);
        else
            RefreshPage();
    }

    // ---------- Развёрнутая медиатека ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LibraryExpandTip), nameof(LibrarySpan))]
    private bool _isLibraryExpanded;

    /// <summary>Развёрнутая медиатека занимает и колонку страницы.</summary>
    public int LibrarySpan => IsLibraryExpanded ? 2 : 1;

    public string LibraryExpandTip => IsLibraryExpanded ? L.T("Свернуть медиатеку") : L.T("Развернуть медиатеку");

    [RelayCommand]
    private void ToggleLibraryExpanded() => IsLibraryExpanded = !IsLibraryExpanded;

    public void Open(CollectionItem item)
    {
        IsLibraryExpanded = false;
        if (item is AllTracksItem)
        {
            GoTracks();
            return;
        }
        Touch(item);
        if ((CurrentPage as CollectionPageViewModel)?.Item == item) return;
        CurrentPage = new CollectionPageViewModel(this, item);
    }

    public void OpenArtist(string name)
    {
        var key = ArtistItem.MakeKey(name);
        if (Artists.FirstOrDefault(a => a.Key == key) is { } artist) Open(artist);
    }

    public void Play(CollectionItem item)
    {
        Touch(item);
        PlayOrToggle(item is FolderItem f ? f.AllTracks : item.Tracks, 0, item.Name);
        // Как в Spotify: включённое поднимается наверх «Недавних».
        if (!SortAlphabetical) RefreshLibraryList();
    }

    /// <summary>Если этот список уже играет, ставит на паузу или продолжает. Иначе запускает с позиции index.</summary>
    public void PlayOrToggle(IReadOnlyList<TrackItem> list, int index, string? title = null)
    {
        if (list.Count == 0) return;
        if (ReferenceEquals(Player.Queue, list) && Player.CurrentTrack != null) Player.PlayPause();
        else Player.PlayList(list, index, title);
    }

    /// <summary>Пересоздаёт открытую страницу после изменений, чтобы она показала свежие данные.</summary>
    private void RefreshPage()
    {
        // Подмена страницы свежей копией не должна попадать в историю «Назад». Прокрутка сохраняется.
        var offset = (CurrentPage as PageViewModel)?.ScrollOffset ?? default;
        _replacingPage = true;
        CurrentPage = CurrentPage switch
        {
            TracksPageViewModel t => new TracksPageViewModel(this, t.Filter),
            SearchPageViewModel => new SearchPageViewModel(this),
            SettingsPageViewModel => new SettingsPageViewModel(this),
            StatsPageViewModel => new StatsPageViewModel(this),
            YearsPageViewModel => new YearsPageViewModel(this),
            CollectionPageViewModel c when Find(c.Item.Kind, c.Item.Key) is { } fresh => new CollectionPageViewModel(this, fresh),
            _ => new HomePageViewModel(this),
        };
        if (CurrentPage is PageViewModel newPage) newPage.ScrollOffset = offset;
        _replacingPage = false;
    }

    private CollectionItem? Find(string kind, string key) => kind switch
    {
        "album" => Albums.FirstOrDefault(a => a.Key == key),
        "artist" => Artists.FirstOrDefault(a => a.Key == key),
        "playlist" => Playlists.FirstOrDefault(p => p.Key == key),
        "liked" => Liked,
        "all" => _allTracks,
        "folder" => Directory.Exists(key) || _library.Any(t => t.Path.StartsWith(key.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            ? Folder(key) : null,
        "smart" => SmartPlaylists.FirstOrDefault(s => s.Key == key),
        _ => null,
    };

    // ---------- Любимые треки ----------

    /// <summary>Если все треки уже в любимых, убирает их оттуда. Иначе добавляет недостающие.</summary>
    public void ToggleLike(IReadOnlyList<TrackItem> tracks)
    {
        if (tracks.Count == 0) return;
        var like = !tracks.All(t => t.IsLiked);
        // Все копии песни (тот же трек в другой папке) отмечаются вместе, иначе сердечко у копии
        // осталось бы пустым, а песня попала бы в «Любимые» второй раз.
        var keys = tracks.Select(t => t.SongKey).ToHashSet();
        var changed = _library.Where(t => keys.Contains(t.SongKey) && t.IsLiked != like)
            .Concat(tracks.Where(t => t.IsLiked != like))
            .Distinct()
            .ToList();
        _db.SetLiked(changed.Select(t => t.Path), like);
        foreach (var t in changed)
        {
            t.IsLiked = like;
            if (like) _likes.Insert(0, t.Path);
            else _likes.RemoveAll(p => string.Equals(p, t.Path, StringComparison.OrdinalIgnoreCase));
        }
        RebuildLiked();
        // Страницу «Любимых» обновляем сразу. Остальные не трогаем, чтобы не сбрасывать прокрутку.
        if (CurrentPage is CollectionPageViewModel { Item: LikedItem }) RefreshPage();
    }

    private void RebuildLiked()
    {
        _liked = new LikedItem(LikedTracks(), Covers, Open, Play);
        OnPropertyChanged(nameof(Liked));
        RefreshLibraryList();
    }

    // ---------- «Сейчас играет» на весь экран ----------

    [ObservableProperty] private bool _isNowPlayingOpen;
    [ObservableProperty] private Avalonia.Media.IBrush _nowPlayingBrush = Gradients.NowPlaying(HaPlayer.Ui.ThemeService.HeaderTint);
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQueueEnd), nameof(UpNextShort))]
    private List<UpNextItem> _upNext = [];

    public bool IsQueueEnd => UpNext.Count == 0;

    /// <summary>Первые треки очереди для правой панели.</summary>
    public List<UpNextItem> UpNextShort => UpNext.Take(8).ToList();

    /// <summary>Правая панель видна, когда её включили и что-то играет.</summary>
    public bool ShowQueuePanel => IsQueuePanelOpen && Player.HasTrack;

    [RelayCommand]
    private void ToggleNowPlaying()
    {
        if (Player.CurrentTrack == null) return;
        IsNowPlayingOpen = !IsNowPlayingOpen;
        if (IsNowPlayingOpen) IsEqualizerOpen = false;
    }

    [RelayCommand]
    private void CloseNowPlaying() => IsNowPlayingOpen = false;

    [RelayCommand]
    private void OpenCurrentArtist()
    {
        if (Player.CurrentTrack is { } t) OpenArtist(t.Artist);
    }

    [RelayCommand]
    private void OpenCurrentAlbum()
    {
        if (Player.CurrentTrack?.Album is { } a) Open(a);
    }

    /// <summary>«Далее в очереди»: сначала поставленное вручную, потом продолжение альбома или плейлиста.</summary>
    private void RefreshUpNext() =>
        UpNext = Player.GetUpNext(50).Select((x, i) => new UpNextItem(x.Track, x.Queued, x.Play, i, Player.RemoveUpcoming)).ToList();

    [RelayCommand]
    private void ClearQueue() => Player.ClearUpcoming();

    /// <summary>Текущий трек и всё, что за ним, одним новым плейлистом.</summary>
    [RelayCommand]
    private async Task SaveQueueAsPlaylist()
    {
        var tracks = new List<TrackItem>();
        if (Player.CurrentTrack is { } current) tracks.Add(current);
        tracks.AddRange(Player.Upcoming());
        if (tracks.Count > 0) await CreatePlaylistAsync(tracks);
    }

    private async Task UpdateNowPlayingAsync()
    {
        RefreshUpNext();

        var track = Player.CurrentTrack;
        if (!Settings.HeaderFromCover)
        {
            NowPlayingBrush = Gradients.NowPlaying(HaPlayer.Ui.ThemeService.HeaderTint);
            return;
        }
        if (track?.Album?.CoverSource is not { } source) return;
        if (await Covers.GetColorAsync(source) is { } c && Player.CurrentTrack == track)
            NowPlayingBrush = Gradients.NowPlaying(c);
    }

    // ---------- Эквалайзер ----------

    [ObservableProperty] private bool _isEqualizerOpen;

    [RelayCommand]
    private void ToggleEqualizer() => IsEqualizerOpen = !IsEqualizerOpen;

    // ---------- Добавление музыки с диска ----------

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";

    [RelayCommand]
    private async Task AddFiles()
    {
        if (Dialogs == null) return;
        var files = await Dialogs.PickFilesAsync();
        if (files.Count > 0) await ImportAsync(files);
    }

    [RelayCommand]
    private async Task AddFolder()
    {
        if (Dialogs == null) return;
        var folder = await Dialogs.PickFolderAsync();
        if (folder == null) return;
        RememberFolder(folder);
        await ImportAsync([folder]);
    }

    /// <summary>
    /// Добавляет файлы и папки в медиатеку. Возвращает треки для всех найденных файлов,
    /// включая те, что уже были в медиатеке (их теги не перечитываются, если файл не менялся).
    /// </summary>
    /// <param name="rescan">
    /// Автоматическая проверка папок: убранные пользователем треки пропускаются.
    /// При явном добавлении (rescan = false) убранные треки возвращаются в медиатеку.
    /// </param>
    public async Task<List<TrackItem>> ImportAsync(IReadOnlyList<string> paths, bool refreshPage = true, bool rescan = false)
    {
        if (IsBusy) return [];
        IsBusy = true;
        BusyText = L.T("Ищу музыку…");
        try
        {
            var known = _library.ToDictionary(t => t.Path, t => t.Model.ModifiedTicks, StringComparer.OrdinalIgnoreCase);
            var progress = new Progress<(int Done, int Total)>(p => BusyText = L.F("Читаю теги: {0} из {1}", p.Done, p.Total));
            var hidden = new HashSet<string>(_hidden, StringComparer.OrdinalIgnoreCase);
            var unhidden = new List<string>();

            var (files, changed) = await Task.Run(() =>
            {
                var found = LibraryScanner.CollectFiles(paths);
                if (rescan)
                {
                    found = found.Where(f => !hidden.Contains(f)).ToList();
                }
                else
                {
                    unhidden.AddRange(found.Where(hidden.Contains));
                    if (unhidden.Count > 0) _db.Unhide(unhidden);
                    // Возвращённые треки перечитываем заново: их строки в базе удалены.
                    foreach (var f in unhidden) known.Remove(f);
                }
                var tracks = LibraryScanner.ReadChanged(found, known, progress);
                _db.Upsert(tracks);
                return (found, tracks);
            });
            foreach (var f in unhidden) _hidden.Remove(f);

            if (changed.Count > 0)
            {
                // Привязку пользователя к альбому и исполнителю переносим в обновлённые данные.
                foreach (var t in changed)
                {
                    if (_byPath.TryGetValue(t.Path, out var existing))
                    {
                        t.UserAlbumKey = existing.Model.UserAlbumKey;
                        t.UserArtist = existing.Model.UserArtist;
                        existing.Update(t);
                    }
                    else
                    {
                        AddTrackItem(new TrackItem(t));
                    }
                }
                Rebuild();
                if (refreshPage) RefreshPage();
            }

            return files.Select(f => _byPath.GetValueOrDefault(f)).OfType<TrackItem>().ToList();
        }
        finally
        {
            IsBusy = false;
            if (rescan) _ = CheckFilesAsync();
        }
    }

    private void AddTrackItem(TrackItem item)
    {
        _library.Add(item);
        _byPath[item.Path] = item;
    }

    /// <summary>Кнопка «Добавить песни» на странице альбома, исполнителя или плейлиста.</summary>
    public async Task AddSongsFromDiskAsync(CollectionItem target)
    {
        if (Dialogs == null) return;
        var files = await Dialogs.PickFilesAsync();
        if (files.Count == 0) return;
        var tracks = await ImportAsync(files, refreshPage: false);
        if (tracks.Count == 0) return;
        switch (target)
        {
            case PlaylistItem p: AddToPlaylist(p, tracks); break;
            case AlbumItem a: AssignToAlbum(a.Key, tracks); break;
            case ArtistItem ar: AssignToArtist(ar.Name, tracks); break;
            case LikedItem:
                ToggleLike(tracks.Where(t => !t.IsLiked).ToList());
                RefreshPage();
                break;
        }
    }

    // ---------- Скачивание по прямой ссылке ----------
    // Общее решение для любого сайта: пользователь сам вставляет ссылку на аудиофайл.
    // Под конкретные сайты ничего не настраивается, страницы не разбираются.

    /// <summary>
    /// Куда сохранять скачанное (по ссылке и из торрентов). Порядок такой:
    /// 1) папка, которую пользователь выбрал сам, если её диск подключён;
    /// 2) папка с музыкой из медиатеки, где бы она ни лежала (первая подключённая);
    /// 3) если папок с музыкой ещё нет — «Музыка\HaPlayer» в профиле Windows.
    /// </summary>
    public string DownloadFolder
    {
        get
        {
            if (IsCustomDownloadFolder && DriveReady(Settings.DownloadFolder!)) return Settings.DownloadFolder!;
            var music = Settings.MusicFolders.FirstOrDefault(Directory.Exists);
            return music ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "HaPlayer");

            static bool DriveReady(string path) => Path.GetPathRoot(path) is { Length: > 0 } root && Directory.Exists(root);
        }
    }

    public bool IsCustomDownloadFolder => !string.IsNullOrWhiteSpace(Settings.DownloadFolder);

    public void SetDownloadFolder(string? folder)
    {
        Settings.DownloadFolder = folder;
        Settings.Save();
    }

    public async Task DownloadFromLinksAsync()
    {
        if (Dialogs == null || IsBusy) return;
        var title = L.T("Скачать по ссылке");
        var text = await Dialogs.PromptAsync(title, L.T("https://…/песня.mp3 (можно несколько через пробел)"), "", L.T("Скачать"));
        if (text == null) return;

        var links = LinkDownloader.ParseLinks(text);
        if (links.Count == 0)
        {
            await Dialogs.MessageAsync(title, L.T("Не вижу ссылки. Она должна начинаться с http:// или https://."));
            return;
        }

        var folder = DownloadFolder;
        var saved = new List<string>();
        var errors = new List<string>();
        IsBusy = true;
        try
        {
            for (var i = 0; i < links.Count; i++)
            {
                var link = links[i];
                var n = links.Count > 1 ? L.F(" {0} из {1}", i + 1, links.Count) : "";
                BusyText = L.F("Скачиваю{0}…", n);
                var progress = new Progress<(long Done, long? Total)>(p =>
                    BusyText = p.Total is > 0 ? L.F("Скачиваю{0}: {1} из {2}", n, Mb(p.Done), Mb(p.Total.Value)) : L.F("Скачиваю{0}: {1}", n, Mb(p.Done)));
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                try
                {
                    saved.Add(await LinkDownloader.DownloadAsync(link, folder, progress, cts.Token));
                }
                catch (LinkException ex)
                {
                    errors.Add(Describe(link, ex.Message));
                }
                catch (OperationCanceledException)
                {
                    errors.Add(Describe(link, L.T("Скачивание шло дольше 15 минут и было остановлено.")));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
                {
                    errors.Add(Describe(link, L.F("Не получилось: {0}", ex.Message)));
                }
            }
        }
        finally
        {
            IsBusy = false;
        }

        List<TrackItem> tracks = [];
        if (saved.Count > 0)
        {
            // Папка загрузок становится папкой медиатеки, чтобы песни находились и при следующих проверках.
            RememberFolder(folder);
            tracks = await ImportAsync(saved);
        }

        if (errors.Count > 0)
        {
            var head = saved.Count > 0 ? L.F("Скачано: {0}. Не получилось: {1}.", saved.Count, errors.Count) + "\n\n" : "";
            await Dialogs.MessageAsync(title, head + string.Join("\n\n", errors));
        }

        if (tracks.Count > 0 &&
            await Dialogs.ConfirmAsync(L.T("Готово"), L.F("{0} в папке\n{1}\n\nВключить сейчас?", Format.Tracks(tracks.Count), folder), "Включить"))
        {
            Player.PlayList(tracks, 0, L.T("Скачано по ссылке"));
        }

        static string Mb(long bytes) => L.F("{0:0.0} МБ", bytes / 1048576.0);

        static string Describe(Uri u, string message)
        {
            var path = Uri.UnescapeDataString(u.AbsolutePath);
            if (path.Length > 40) path = "…" + path[^39..];
            return $"{u.Host}{path}\n{message}";
        }
    }

    // ---------- Плейлисты ----------

    [RelayCommand]
    private async Task CreatePlaylist() => await CreatePlaylistAsync([]);

    public async Task CreatePlaylistAsync(IReadOnlyList<TrackItem> tracks)
    {
        if (Dialogs == null) return;
        var name = await Dialogs.PromptAsync(L.T("Новый плейлист"), L.T("Название"), L.F("Мой плейлист № {0}", Playlists.Count + 1), "Создать");
        if (name == null) return;
        var id = _db.CreatePlaylist(name);
        var data = new PlaylistData(id, name, tracks.Select(t => t.Path).ToList());
        _playlistData.Add(data);
        if (data.Paths.Count > 0) _db.SetPlaylistTracks(id, data.Paths);
        Rebuild();
        if (Find("playlist", id.ToString()) is { } created) Open(created);
    }

    // ---------- Импорт плейлистов из других плееров ----------

    [RelayCommand]
    private async Task ImportPlaylists()
    {
        if (Dialogs == null) return;

        var lists = new List<ImportedPlaylist>();
        var winamp = await Task.Run(PlaylistImporter.FindWinampPlaylists);
        if (winamp.Count > 0 && await Dialogs.ConfirmAsync(L.T("Плейлисты Winamp"),
                L.F("В Winamp нашлось плейлистов: {0}. Перенести их в HaPlayer?\n\nЕсли нужен другой плеер, нажмите «Отмена» и выберите файлы плейлистов.", winamp.Count),
                L.T("Перенести")))
        {
            lists.AddRange(winamp);
        }
        else
        {
            var files = await Dialogs.PickPlaylistFilesAsync();
            if (files.Count == 0) return;
            lists.AddRange(await Task.Run(() => files.SelectMany(PlaylistImporter.Read).ToList()));
        }

        lists.RemoveAll(l => l.Paths.Count == 0);
        if (lists.Count == 0)
        {
            await Dialogs.MessageAsync(L.T("Импорт плейлистов"), L.T("В выбранных файлах не нашлось песен с диска. Ссылки на интернет-радио пропускаются."));
            return;
        }

        // Сначала добавляем в медиатеку все песни, которые есть на диске.
        var existing = await Task.Run(() => lists.SelectMany(l => l.Paths).Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToList());
        if (existing.Count > 0) await ImportAsync(existing, refreshPage: false);

        var missing = 0;
        long? firstId = null;
        foreach (var list in lists)
        {
            var paths = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in list.Paths)
            {
                if (!_byPath.ContainsKey(p)) missing++;
                else if (seen.Add(p)) paths.Add(p);
            }
            if (paths.Count == 0) continue;

            var name = UniquePlaylistName(list.Name);
            var id = _db.CreatePlaylist(name);
            _db.SetPlaylistTracks(id, paths);
            _playlistData.Add(new PlaylistData(id, name, paths));
            firstId ??= id;
        }

        Rebuild();
        var created = _playlistData.Count(p => p.Id >= (firstId ?? long.MaxValue));
        if (firstId is { } fid && Find("playlist", fid.ToString()) is { } first) Open(first);
        else RefreshPage();

        var text = L.F("Перенесено плейлистов: {0}.", created);
        if (missing > 0) text += "\n" + L.F("Песен не нашлось на диске: {0}. Возможно, файлы перемещены или диск не подключён.", missing);
        await Dialogs.MessageAsync(L.T("Импорт плейлистов"), text);
    }

    private string UniquePlaylistName(string name)
    {
        var taken = new HashSet<string>(_playlistData.Select(p => p.Name), StringComparer.CurrentCultureIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++)
            if (!taken.Contains($"{name} ({i})")) return $"{name} ({i})";
    }

    public void AddToPlaylist(PlaylistItem playlist, IEnumerable<TrackItem> tracks)
    {
        var data = _playlistData.First(p => p.Id == playlist.Id);
        var existing = new HashSet<string>(data.Paths, StringComparer.OrdinalIgnoreCase);
        foreach (var t in tracks)
            if (existing.Add(t.Path)) data.Paths.Add(t.Path);
        _db.SetPlaylistTracks(data.Id, data.Paths);
        Rebuild();
        RefreshPage();
    }

    public void RemoveFromPlaylist(PlaylistItem playlist, IEnumerable<TrackItem> tracks)
    {
        var data = _playlistData.First(p => p.Id == playlist.Id);
        var remove = new HashSet<string>(tracks.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);
        data.Paths.RemoveAll(remove.Contains);
        _db.SetPlaylistTracks(data.Id, data.Paths);
        Rebuild();
        RefreshPage();
    }

    public async Task RenamePlaylistAsync(PlaylistItem playlist)
    {
        if (Dialogs == null) return;
        var name = await Dialogs.PromptAsync(L.T("Переименовать плейлист"), L.T("Название"), playlist.Name, L.T("Сохранить"));
        if (name == null || name == playlist.Name) return;
        _db.RenamePlaylist(playlist.Id, name);
        var i = _playlistData.FindIndex(p => p.Id == playlist.Id);
        _playlistData[i] = _playlistData[i] with { Name = name };
        Rebuild();
        RefreshPage();
    }

    public async Task DeletePlaylistAsync(PlaylistItem playlist)
    {
        if (Dialogs == null) return;
        var ok = await Dialogs.ConfirmAsync(L.T("Удалить плейлист?"),
            L.F("Плейлист «{0}» исчезнет из HaPlayer. Сами песни останутся в медиатеке и на диске.", playlist.Name), "Удалить");
        if (!ok) return;
        _db.DeletePlaylist(playlist.Id);
        _playlistData.RemoveAll(p => p.Id == playlist.Id);
        _images.Remove(("playlist", playlist.Key));
        Rebuild();
        CurrentPage = new HomePageViewModel(this);
    }

    // ---------- Альбомы и исполнители ----------

    public void AssignToAlbum(string albumKey, IReadOnlyList<TrackItem> tracks)
    {
        _db.SetUserAlbum(tracks.Select(t => t.Path), albumKey);
        foreach (var t in tracks)
        {
            t.Model.UserAlbumKey = albumKey;
            t.Refresh();
        }
        Rebuild();
        RefreshPage();
    }

    public void AssignToArtist(string artist, IReadOnlyList<TrackItem> tracks)
    {
        _db.SetUserArtist(tracks.Select(t => t.Path), artist);
        foreach (var t in tracks)
        {
            t.Model.UserArtist = artist;
            t.Refresh();
        }
        Rebuild();
        RefreshPage();
    }

    // ---------- Исполнитель из папки дискографии ----------

    /// <summary>
    /// Папка вида «Исполнитель\Альбом 1, Альбом 2, …»: все песни внутри становятся песнями одного исполнителя,
    /// даже если в тегах написано по-разному («feat.», опечатки, пустой тег). Альбомы берутся из тегов.
    /// Файлы не меняются: привязка хранится в базе HaPlayer и сбрасывается через «Вернуть из тегов».
    /// </summary>
    public async Task AddArtistFolderAsync()
    {
        if (Dialogs == null || IsBusy) return;
        var title = L.T("Исполнитель из папки");
        var folder = await Dialogs.PickFolderAsync();
        if (folder == null) return;

        var name = await Dialogs.PromptAsync(title, L.T("Имя исполнителя"), GuessArtistName(folder), L.T("Добавить"));
        if (name == null) return;

        RememberFolder(folder);
        var tracks = await ImportAsync([folder], refreshPage: false);
        if (tracks.Count == 0)
        {
            await Dialogs.MessageAsync(title, L.T("В этой папке не нашлось музыки, которую умеет играть HaPlayer."));
            return;
        }
        AssignToArtist(name, tracks);
        if (Find("artist", ArtistItem.MakeKey(name)) is { } artist) Open(artist);
    }

    /// <summary>«Metallica - Discography (1983-2016) [FLAC]» → «Metallica».</summary>
    public static string GuessArtistName(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        name = System.Text.RegularExpressions.Regex.Replace(name,
            @"\s*[-–—_]*\s*(discography|дискография|collection|коллекция|all albums|все альбомы)\b.*$", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s*[\(\[\{][^\)\]\}]*[\)\]\}]\s*", " ");
        return name.Replace('_', ' ').Trim(' ', '-', '–', '—', '.');
    }

    public async Task AssignToNewAlbumAsync(IReadOnlyList<TrackItem> tracks)
    {
        if (Dialogs == null) return;
        var name = await Dialogs.PromptAsync(L.T("Новый альбом"), L.T("Название альбома"), "", L.T("Создать"));
        if (name != null) AssignToAlbum(Track.MakeAlbumKey(name, null), tracks);
    }

    public async Task AssignToNewArtistAsync(IReadOnlyList<TrackItem> tracks)
    {
        if (Dialogs == null) return;
        var name = await Dialogs.PromptAsync(L.T("Новый исполнитель"), L.T("Имя исполнителя"), "", L.T("Готово"));
        if (name != null) AssignToArtist(name, tracks);
    }

    /// <summary>Возвращает трекам альбом и исполнителя из тегов файла.</summary>
    public void ResetAssignments(IReadOnlyList<TrackItem> tracks)
    {
        var paths = tracks.Select(t => t.Path).ToList();
        _db.SetUserAlbum(paths, null);
        _db.SetUserArtist(paths, null);
        foreach (var t in tracks)
        {
            t.Model.UserAlbumKey = null;
            t.Model.UserArtist = null;
            t.Refresh();
        }
        Rebuild();
        RefreshPage();
    }

    // ---------- Фото ----------

    public async Task ChangePhotoAsync(CollectionItem item)
    {
        if (Dialogs == null) return;
        var source = await Dialogs.PickImageAsync();
        if (source == null) return;

        // Копируем картинку к себе: если пользователь потом удалит или переместит оригинал, фото останется.
        var target = Path.Combine(AppPaths.ImagesDirectory, $"{item.Kind}-{Guid.NewGuid():N}{Path.GetExtension(source).ToLowerInvariant()}");
        try
        {
            await Task.Run(() => File.Copy(source, target));
        }
        catch
        {
            return;
        }

        _db.SetImage(item.Kind, item.Key, target);
        _images[(item.Kind, item.Key)] = target;
        Rebuild();
        RefreshPage();
    }

    // ---------- Сборка коллекций ----------

    private string? Image(string kind, string key) => _images.GetValueOrDefault((kind, key));

    private void Rebuild()
    {
        RebuildAlbums();
        RebuildArtists();
        RebuildPlaylists();
        RefreshLibraryList();
        OnPropertyChanged(nameof(Library));
    }

    private void RebuildAlbums()
    {
        var groups = new Dictionary<string, List<TrackItem>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var t in _library)
        {
            var key = t.Model.AlbumKey;
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = [];
                order.Add(key);
            }
            list.Add(t);
        }

        var albums = new List<AlbumItem>(order.Count);
        foreach (var key in order)
        {
            var tracks = groups[key]
                .OrderBy(t => t.Model.DiscNo)
                .ThenBy(t => t.Model.TrackNo == 0 ? int.MaxValue : t.Model.TrackNo)
                .ThenBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var first = tracks[0].Model;
            var artists = tracks.Select(t => t.Artist).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var artist = first.UserAlbumKey == null && first.AlbumArtist != null
                ? first.AlbumArtist
                : artists == 1 ? tracks[0].Artist : L.T("Разные исполнители");
            var year = tracks.Select(t => t.Model.Year).FirstOrDefault(y => y > 0);

            var album = new AlbumItem(key, L.Data(first.EffectiveAlbum), L.Data(artist), year, tracks, Covers, Image("album", key), Open, Play);
            foreach (var t in tracks) t.Album = album;
            albums.Add(album);
        }

        RecentAlbums = Enumerable.Reverse(albums).ToList();
        Replace(Albums, albums.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase));
        OnPropertyChanged(nameof(HasNoAlbums));
    }

    private void RebuildArtists()
    {
        var artists = _library
            .GroupBy(t => ArtistItem.MakeKey(t.Artist))
            .Select(g =>
            {
                var tracks = g
                    .OrderBy(t => t.AlbumName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(t => t.Model.DiscNo)
                    .ThenBy(t => t.Model.TrackNo)
                    .ToList();
                var name = tracks[0].Artist;
                return new ArtistItem(name, tracks, Covers, Image("artist", ArtistItem.MakeKey(name)), Open, Play);
            })
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase);
        Replace(Artists, artists);
        OnPropertyChanged(nameof(HasNoArtists));
    }

    private void RebuildPlaylists()
    {
        var playlists = _playlistData.Select(p => new PlaylistItem(
            p.Id, p.Name,
            PinnedFirst(p.Id, p.Paths.Select(x => _byPath.GetValueOrDefault(x)).OfType<TrackItem>().ToList()),
            Covers, Image("playlist", p.Id.ToString()), Open, Play)).ToList();
        Replace(Playlists, playlists);

        _liked = new LikedItem(LikedTracks(), Covers, Open, Play);
        _allTracks = new AllTracksItem(_library, Covers, Open, Play);
        OnPropertyChanged(nameof(Liked));
    }

    /// <summary>Любимые треки, новые сверху. Копии одной песни показываются один раз.</summary>
    private List<TrackItem> LikedTracks()
    {
        var seen = new HashSet<string>();
        var result = new List<TrackItem>();
        foreach (var p in _likes)
            if (_byPath.TryGetValue(p, out var t) && seen.Add(t.SongKey)) result.Add(t);
        return result;
    }

    private void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        _syncingSelection = true;
        target.Clear();
        foreach (var i in items) target.Add(i);
        _syncingSelection = false;
    }
}
