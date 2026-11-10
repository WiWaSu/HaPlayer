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

        foreach (var t in db.LoadAll()) AddTrackItem(new TrackItem(t));
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
        Player.QueueChanged += RefreshUpNext;
        _currentPage = new HomePageViewModel(this);
        Rebuild();

        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.CurrentTrack))
            {
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

    public string SettingsTip => IsSettingsActive ? "Закрыть настройки (Esc)" : "Настройки";

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
        var ok = await Dialogs.ConfirmAsync("Убрать папку из медиатеки?",
            $"{folder}\n\nИз HaPlayer исчезнут треки из этой папки: {count}. Файлы на диске останутся на месте.", "Убрать");
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

    /// <summary>Перезапуск программы. Задаёт App: ему виден жизненный цикл окна.</summary>
    public Action? RestartRequested { get; set; }

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
            await Dialogs.MessageAsync("Данные уже здесь", target);
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
            await Dialogs.MessageAsync("Нет доступа к папке",
                $"В папку {target} нельзя записывать.\n\nЕсли программа лежит в Program Files, для портативного режима переместите её, например, на рабочий стол или флешку.");
            return;
        }

        var targetDb = Path.Combine(target, "library.db");
        if (File.Exists(targetDb))
        {
            var useExisting = await Dialogs.ConfirmAsync("Здесь уже есть данные HaPlayer",
                $"{target}\n\nОткрыть их? Текущая медиатека не перенесётся и останется на старом месте.", "Открыть их");
            if (!useExisting)
            {
                if (mode == DataMode.Portable && AppPaths.Mode != DataMode.Portable) File.Delete(AppPaths.PortableMarker);
                return;
            }
        }
        else
        {
            var ok = await Dialogs.ConfirmAsync("Перенести данные?",
                $"Медиатека, плейлисты, любимые треки, фото и настройки будут скопированы сюда:\n{target}\n\nСтарая копия останется на месте. HaPlayer перезапустится.",
                "Перенести");
            if (!ok)
            {
                if (mode == DataMode.Portable && AppPaths.Mode != DataMode.Portable) File.Delete(AppPaths.PortableMarker);
                return;
            }

            SaveSession();
            IsBusy = true;
            BusyText = "Переношу данные…";
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
                await Dialogs.MessageAsync("Не получилось перенести данные", e.Message);
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
                _ => new HomePageViewModel(this),
            };
        }
        // Стартовая главная, с которой ушли при восстановлении, в историю «Назад» не нужна.
        _back.Clear();
        OnPropertyChanged(nameof(CanGoBack));

        // Очередь собираем заново из путей. Файлы, которых больше нет в медиатеке, пропускаем.
        var saved = Settings.LastQueue;
        if (saved.Count == 0 || Settings.LastIndex < 0 || Settings.LastIndex >= saved.Count) return;
        var current = saved[Settings.LastIndex];
        var queue = saved.Select(p => _byPath.GetValueOrDefault(p)).OfType<TrackItem>().ToList();
        var index = queue.FindIndex(t => string.Equals(t.Path, current, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;

        // Если очередь совпадает со списком на открытой странице, берём тот же список:
        // тогда кнопка «играть» на странице сработает как пауза, а не начнёт сначала.
        IReadOnlyList<TrackItem> list = queue;
        if (CurrentPage is CollectionPageViewModel c && c.Item.Tracks.SequenceEqual(queue)) list = c.Item.Tracks;
        else if (Library.SequenceEqual(queue)) list = Library;

        Player.Restore(list, index, Settings.LastPosition, Settings.LastQueueTitle);
    }

    public void ApplyTheme()
    {
        HaPlayer.Ui.ThemeService.Apply(Settings.Palette, Settings.Surface);
        Rebuild();
        if (CurrentPage is not SettingsPageViewModel) RefreshPage();
        NowPlayingBrush = Gradients.NowPlaying(HaPlayer.Ui.ThemeService.HeaderTint);
        _ = UpdateNowPlayingAsync();
    }

    /// <summary>Строки таблицы для списка треков. Общая фабрика, чтобы у всех страниц одинаково работали кнопки строки.</summary>
    public List<TrackRow> MakeRows(IReadOnlyList<TrackItem> list, string? queueTitle, Func<TrackItem, int, string> number,
        Func<TrackItem, string> secondary, bool showCover) =>
        list.Select((t, i) => new TrackRow(i, number(t, i), t, secondary(t), showCover,
            play: () => Player.PlayList(list, i, queueTitle),
            toggle: () =>
            {
                if (t.IsCurrent) Player.PlayPause();
                else Player.PlayList(list, i, queueTitle);
            },
            like: () => ToggleLike([t]))).ToList();

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
    [NotifyPropertyChangedFor(nameof(IsPlaylistsTab), nameof(IsAlbumsTab), nameof(IsArtistsTab), nameof(HasFilter))]
    private LibraryTab _tab = LibraryTab.All;

    public bool IsPlaylistsTab => Tab == LibraryTab.Playlists;
    public bool IsAlbumsTab => Tab == LibraryTab.Albums;
    public bool IsArtistsTab => Tab == LibraryTab.Artists;
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

    public string LibrarySortText => SortAlphabetical ? "По алфавиту" : "Недавние";

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
    [NotifyPropertyChangedFor(nameof(IsHomeActive), nameof(IsTracksActive), nameof(IsSearchActive), nameof(IsSettingsActive), nameof(SettingsTip))]
    private object _currentPage;

    public bool IsHomeActive => CurrentPage is HomePageViewModel;
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
        ? "Здесь появится то, что вы слушали"
        : "История прослушивания выключена";

    public string HistoryEmptyText => Settings.KeepHistory
        ? "Сюда попадает всё, что звучало дольше 30 секунд. Хранится только на этом компьютере."
        : "Включается в настройках, раздел «Данные».";

    private void LoadHistory()
    {
        var rows = _db.LoadHistory(200);
        var items = new List<HistoryItem>();
        foreach (var (path, ticks) in rows)
        {
            if (!_byPath.TryGetValue(path, out var t)) continue;
            var when = new DateTime(ticks, DateTimeKind.Utc).ToLocalTime();
            items.Add(new HistoryItem(t, when, () => Player.PlayList([t], 0, "История прослушивания")));
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
        if (IsHistoryPanelOpen) LoadHistory();
    }

    [RelayCommand]
    private async Task ClearHistory()
    {
        if (Dialogs == null || !HasHistory) return;
        var ok = await Dialogs.ConfirmAsync("Очистить историю?",
            "Список прослушанных треков удалится с этого компьютера. Сами треки останутся в медиатеке.", "Очистить");
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
        var ok = await Dialogs.ConfirmAsync($"Убрать {item.KindName} из медиатеки?",
            $"«{item.Name}»: {Format.Tracks(tracks.Count)} исчезнут из HaPlayer, плейлистов и истории.\n\n" +
            "Файлы на диске останутся на месте. При проверке папок они не вернутся. Чтобы вернуть, добавьте их снова через «Настройки → Добавить файлы».",
            "Убрать");
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

    public string LibraryExpandTip => IsLibraryExpanded ? "Свернуть медиатеку" : "Развернуть медиатеку";

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
        PlayOrToggle(item.Tracks, 0, item.Name);
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
        // Подмена страницы свежей копией не должна попадать в историю «Назад».
        _replacingPage = true;
        CurrentPage = CurrentPage switch
        {
            TracksPageViewModel => new TracksPageViewModel(this),
            SearchPageViewModel => new SearchPageViewModel(this),
            SettingsPageViewModel => new SettingsPageViewModel(this),
            CollectionPageViewModel c when Find(c.Item.Kind, c.Item.Key) is { } fresh => new CollectionPageViewModel(this, fresh),
            _ => new HomePageViewModel(this),
        };
        _replacingPage = false;
    }

    private CollectionItem? Find(string kind, string key) => kind switch
    {
        "album" => Albums.FirstOrDefault(a => a.Key == key),
        "artist" => Artists.FirstOrDefault(a => a.Key == key),
        "playlist" => Playlists.FirstOrDefault(p => p.Key == key),
        "liked" => Liked,
        "all" => _allTracks,
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
        UpNext = Player.GetUpNext(30).Select(x => new UpNextItem(x.Track, x.Queued, x.Play)).ToList();

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
        BusyText = "Ищу музыку…";
        try
        {
            var known = _library.ToDictionary(t => t.Path, t => t.Model.ModifiedTicks, StringComparer.OrdinalIgnoreCase);
            var progress = new Progress<(int Done, int Total)>(p => BusyText = $"Читаю теги: {p.Done} из {p.Total}");
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
        const string title = "Скачать по ссылке";
        var text = await Dialogs.PromptAsync(title, "https://…/песня.mp3 (можно несколько через пробел)", "", "Скачать");
        if (text == null) return;

        var links = LinkDownloader.ParseLinks(text);
        if (links.Count == 0)
        {
            await Dialogs.MessageAsync(title, "Не вижу ссылки. Она должна начинаться с http:// или https://.");
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
                var n = links.Count > 1 ? $" {i + 1} из {links.Count}" : "";
                BusyText = $"Скачиваю{n}…";
                var progress = new Progress<(long Done, long? Total)>(p =>
                    BusyText = p.Total is > 0 ? $"Скачиваю{n}: {Mb(p.Done)} из {Mb(p.Total.Value)}" : $"Скачиваю{n}: {Mb(p.Done)}");
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
                    errors.Add(Describe(link, "Скачивание шло дольше 15 минут и было остановлено."));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
                {
                    errors.Add(Describe(link, $"Не получилось: {ex.Message}"));
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
            var head = saved.Count > 0 ? $"Скачано: {saved.Count}. Не получилось: {errors.Count}.\n\n" : "";
            await Dialogs.MessageAsync(title, head + string.Join("\n\n", errors));
        }

        if (tracks.Count > 0 &&
            await Dialogs.ConfirmAsync("Готово", $"{Format.Tracks(tracks.Count)} в папке\n{folder}\n\nВключить сейчас?", "Включить"))
        {
            Player.PlayList(tracks, 0, "Скачано по ссылке");
        }

        static string Mb(long bytes) => $"{bytes / 1048576.0:0.0} МБ";

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
        var name = await Dialogs.PromptAsync("Новый плейлист", "Название", $"Мой плейлист № {Playlists.Count + 1}", "Создать");
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
        if (winamp.Count > 0 && await Dialogs.ConfirmAsync("Плейлисты Winamp",
                $"В Winamp нашлось плейлистов: {winamp.Count}. Перенести их в HaPlayer?\n\nЕсли нужен другой плеер, нажмите «Отмена» и выберите файлы плейлистов.",
                "Перенести"))
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
            await Dialogs.MessageAsync("Импорт плейлистов", "В выбранных файлах не нашлось песен с диска. Ссылки на интернет-радио пропускаются.");
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

        var text = $"Перенесено плейлистов: {created}.";
        if (missing > 0) text += $"\nПесен не нашлось на диске: {missing}. Возможно, файлы перемещены или диск не подключён.";
        await Dialogs.MessageAsync("Импорт плейлистов", text);
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
        var name = await Dialogs.PromptAsync("Переименовать плейлист", "Название", playlist.Name, "Сохранить");
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
        var ok = await Dialogs.ConfirmAsync("Удалить плейлист?",
            $"Плейлист «{playlist.Name}» исчезнет из HaPlayer. Сами песни останутся в медиатеке и на диске.", "Удалить");
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
        const string title = "Исполнитель из папки";
        var folder = await Dialogs.PickFolderAsync();
        if (folder == null) return;

        var name = await Dialogs.PromptAsync(title, "Имя исполнителя", GuessArtistName(folder), "Добавить");
        if (name == null) return;

        RememberFolder(folder);
        var tracks = await ImportAsync([folder], refreshPage: false);
        if (tracks.Count == 0)
        {
            await Dialogs.MessageAsync(title, "В этой папке не нашлось музыки, которую умеет играть HaPlayer.");
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
        var name = await Dialogs.PromptAsync("Новый альбом", "Название альбома", "", "Создать");
        if (name != null) AssignToAlbum(Track.MakeAlbumKey(name, null), tracks);
    }

    public async Task AssignToNewArtistAsync(IReadOnlyList<TrackItem> tracks)
    {
        if (Dialogs == null) return;
        var name = await Dialogs.PromptAsync("Новый исполнитель", "Имя исполнителя", "", "Готово");
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
                : artists == 1 ? tracks[0].Artist : "Разные исполнители";
            var year = tracks.Select(t => t.Model.Year).FirstOrDefault(y => y > 0);

            var album = new AlbumItem(key, first.EffectiveAlbum, artist, year, tracks, Covers, Image("album", key), Open, Play);
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
            p.Paths.Select(x => _byPath.GetValueOrDefault(x)).OfType<TrackItem>().ToList(),
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
