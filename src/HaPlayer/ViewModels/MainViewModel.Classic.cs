using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlayer.Ui;

namespace HaPlayer.ViewModels;

/// <summary>
/// Классический вид: одно плотное окно с панелью «сейчас играет», деревом медиатеки,
/// вкладками и таблицей треков. Данные общие с современным видом, отличается только раскладка.
/// </summary>
public sealed partial class MainViewModel
{
    // ---------- Выбор вида ----------

    public bool IsClassic => Settings.ViewMode == "classic";
    public bool IsModern => !IsClassic;

    /// <summary>Первый запуск: вид ещё не выбран, поверх окна экран выбора.</summary>
    public bool IsViewChooserOpen => Settings.ViewMode == null;

    /// <summary>Окно перестраивается под другой вид. Задаёт MainWindow.</summary>
    public event Action? ViewModeChanged;

    [RelayCommand]
    private void ChooseView(string mode) => SetViewMode(mode);

    public void SetViewMode(string mode)
    {
        Settings.ViewMode = mode == "classic" ? "classic" : "modern";
        Settings.Save();
        if (IsClassic) EnsureClassic();
        OnPropertyChanged(nameof(IsClassic));
        OnPropertyChanged(nameof(IsModern));
        OnPropertyChanged(nameof(IsViewChooserOpen));
        ViewModeChanged?.Invoke();
    }

    /// <summary>Настройки и «Итоги» в классическом виде открываются на месте таблицы.</summary>
    public bool IsClassicPage => CurrentPage is SettingsPageViewModel or StatsPageViewModel;

    // ---------- Дерево медиатеки ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTreeArtists), nameof(IsTreeAlbums), nameof(IsTreeFolders), nameof(IsTreePlaylists))]
    private ClassicTreeMode _treeMode = ClassicTreeMode.Artists;

    public bool IsTreeArtists => TreeMode == ClassicTreeMode.Artists;
    public bool IsTreeAlbums => TreeMode == ClassicTreeMode.Albums;
    public bool IsTreeFolders => TreeMode == ClassicTreeMode.Folders;
    public bool IsTreePlaylists => TreeMode == ClassicTreeMode.Playlists;

    [ObservableProperty] private List<ClassicNode> _classicTree = [];

    [RelayCommand]
    private void SetTreeMode(string mode) => TreeMode = Enum.Parse<ClassicTreeMode>(mode);

    partial void OnTreeModeChanged(ClassicTreeMode value)
    {
        Settings.ClassicTreeMode = value.ToString();
        Settings.Save();
        BuildClassicTree();
    }

    private void BuildClassicTree()
    {
        ClassicTree = TreeMode switch
        {
            ClassicTreeMode.Artists => Artists
                .Select(a => new ClassicNode(a.Name, Format.Tracks(a.Tracks.Count), Icons.Person, a, () => ArtistAlbumNodes(a)))
                .ToList(),
            ClassicTreeMode.Albums => Albums
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(a => new ClassicNode(a.Name, a.Year > 0 ? $"{a.Artist} · {a.Year}" : a.Artist, Icons.Library, a))
                .ToList(),
            ClassicTreeMode.Folders => RootFolders().OfType<FolderItem>().Select(FolderNode).ToList(),
            _ => PlaylistNodes(),
        };
    }

    /// <summary>Альбомы исполнителя в том же порядке, что на его странице.</summary>
    private List<ClassicNode> ArtistAlbumNodes(ArtistItem artist)
    {
        var byYear = Albums
            .Where(a => a.Tracks.Any(t => ArtistItem.MakeKey(t.Artist) == artist.Key))
            .OrderByDescending(a => a.Year)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return OrderAlbums(artist.Key, byYear)
            .Select(a => new ClassicNode(a.Name, a.Year > 0 ? a.Year.ToString() : "", Icons.Library, a))
            .ToList();
    }

    private ClassicNode FolderNode(FolderItem folder) =>
        new(folder.Name, Format.Tracks(folder.AllTracks.Count), Icons.Folder, folder,
            () => SubFolders(folder).Select(FolderNode).ToList());

    private List<ClassicNode> PlaylistNodes()
    {
        ClassicNode Node(CollectionItem p, Avalonia.Media.Geometry icon) => new(p.Name, Format.Tracks(p.Tracks.Count), icon, p);
        var nodes = new List<ClassicNode>
        {
            Node(_allTracks, Icons.Library),
            Node(_liked, Icons.Heart),
        };
        var inFolder = PlaylistFolderItems.SelectMany(f => f.Playlists).Select(p => p.Id).ToHashSet();
        nodes.AddRange(PlaylistFolderItems.Select(f => new ClassicNode(f.Name, f.Subtitle, Icons.Folder, f,
            () => f.Playlists.Select(p => Node(p, Icons.Note)).ToList())));
        nodes.AddRange(Playlists.Where(p => !inFolder.Contains(p.Id)).Select(p => Node(p, Icons.Note)));
        nodes.AddRange(UserSmart.Select(s => Node(s, Icons.Sparkles)));
        return nodes;
    }

    // ---------- Вкладки и таблица ----------

    public ObservableCollection<ClassicTab> ClassicTabs { get; } = [];

    [ObservableProperty] private ClassicListViewModel? _classicList;

    /// <summary>Поиск над таблицей: фильтрует открытую вкладку по названию, исполнителю, альбому и имени файла.</summary>
    [ObservableProperty] private string _classicFilter = "";

    partial void OnClassicFilterChanged(string value) => ClassicList?.SetFilter(value);

    private bool _classicReady;

    /// <summary>Готовит классический вид: восстанавливает вкладки прошлого раза и дерево.</summary>
    private void EnsureClassic()
    {
        if (_classicReady) return;
        _classicReady = true;
        if (Enum.TryParse<ClassicTreeMode>(Settings.ClassicTreeMode, out var mode)) _treeMode = mode;
        OnPropertyChanged(nameof(TreeMode));
        OnPropertyChanged(nameof(IsTreeArtists));
        OnPropertyChanged(nameof(IsTreeAlbums));
        OnPropertyChanged(nameof(IsTreeFolders));
        OnPropertyChanged(nameof(IsTreePlaylists));
        BuildClassicTree();

        foreach (var key in Settings.ClassicTabs)
            if (FindByKey(key) is { } item && ClassicTabs.All(t => t.Key != key))
                ClassicTabs.Add(new ClassicTab(item, SelectClassicTab, CloseClassicTab));
        if (ClassicTabs.Count == 0) ClassicTabs.Add(new ClassicTab(_allTracks, SelectClassicTab, CloseClassicTab));
        // Без закрытия настроек: вид переключают как раз из них.
        SelectClassicTab(ClassicTabs.FirstOrDefault(t => t.Key == Settings.ClassicActiveTab) ?? ClassicTabs[0], closePage: false);
    }

    private CollectionItem? FindByKey(string key)
    {
        var at = key.IndexOf(':');
        return at < 0 ? null : Find(key[..at], key[(at + 1)..]);
    }

    /// <summary>Открывает коллекцию во вкладке: уже открытая становится текущей, новая добавляется справа.</summary>
    public void OpenInClassic(CollectionItem item)
    {
        EnsureClassic();
        var key = item.Kind + ":" + item.Key;
        var tab = ClassicTabs.FirstOrDefault(t => t.Key == key);
        if (tab == null)
        {
            tab = new ClassicTab(item, SelectClassicTab, CloseClassicTab);
            ClassicTabs.Add(tab);
            // Вкладок не больше двенадцати: лишние закрываются, начиная с самых левых.
            while (ClassicTabs.Count > 12) ClassicTabs.Remove(ClassicTabs.First(t => !t.IsActive));
        }
        Touch(item);
        SelectClassicTab(tab);
    }

    private static IReadOnlyList<TrackItem> ClassicTracks(CollectionItem item) => item is FolderItem f ? f.AllTracks : item.Tracks;

    private void SelectClassicTab(ClassicTab tab) => SelectClassicTab(tab, closePage: true);

    private void SelectClassicTab(ClassicTab tab, bool closePage)
    {
        // Настройки или «Итоги», открытые на месте таблицы, закрываются: пользователь выбрал список.
        if (closePage && IsClassicPage) GoHome();
        foreach (var t in ClassicTabs) t.IsActive = t == tab;
        _classicFilter = "";
        OnPropertyChanged(nameof(ClassicFilter));
        ClassicList = new ClassicListViewModel(this, tab.Item, ClassicTracks(tab.Item), "");
        SaveClassicTabs();
    }

    private void CloseClassicTab(ClassicTab tab)
    {
        if (ClassicTabs.Count <= 1) return;
        var at = ClassicTabs.IndexOf(tab);
        ClassicTabs.Remove(tab);
        if (tab.IsActive) SelectClassicTab(ClassicTabs[Math.Min(at, ClassicTabs.Count - 1)]);
        else SaveClassicTabs();
    }

    private void SaveClassicTabs()
    {
        Settings.ClassicTabs = ClassicTabs.Select(t => t.Key).ToList();
        Settings.ClassicActiveTab = ClassicTabs.FirstOrDefault(t => t.IsActive)?.Key;
        Settings.Save();
    }

    /// <summary>Медиатека пересобрана: вкладки получают свежие коллекции, таблица и дерево перестраиваются.</summary>
    private void RefreshClassic()
    {
        if (!_classicReady) return;
        foreach (var tab in ClassicTabs.ToList())
        {
            if (FindByKey(tab.Key) is { } fresh)
            {
                tab.Item = fresh;
                tab.Refresh();
            }
            else if (ClassicTabs.Count > 1) ClassicTabs.Remove(tab);
        }
        if (ClassicTabs.FirstOrDefault(t => t.IsActive) is not { } active)
        {
            if (ClassicTabs.Count == 0) return;
            active = ClassicTabs[0];
            active.IsActive = true;
        }
        ClassicList = new ClassicListViewModel(this, active.Item, ClassicTracks(active.Item), ClassicFilter);
        BuildClassicTree();
    }

    /// <summary>Двойной щелчок по узлу дерева: открыть во вкладке и сразу включить.</summary>
    public void PlayFromTree(CollectionItem item)
    {
        OpenInClassic(item);
        var tracks = ClassicTracks(item);
        if (tracks.Count > 0) Player.PlayList(tracks, 0, item.Name);
    }
}
