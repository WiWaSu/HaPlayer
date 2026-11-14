using HaPlayer.Ui;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HaPlayer.ViewModels;

public abstract class PageViewModel(MainViewModel main) : ObservableObject
{
    public MainViewModel Main { get; } = main;
    public IAsyncRelayCommand AddFolderCommand => Main.AddFolderCommand;
    public IAsyncRelayCommand AddFilesCommand => Main.AddFilesCommand;

    /// <summary>Где была прокрутка, когда со страницы ушли. При возврате «Назад» список встаёт туда же.</summary>
    public Avalonia.Vector ScrollOffset { get; set; }

    /// <summary>Плейлист, если страница его показывает. Нужен меню трека для пункта «Убрать из плейлиста».</summary>
    public virtual PlaylistItem? Playlist => null;
}

public static class Gradients
{
    public static Color Panel => HaPlayer.Ui.ThemeService.CurrentSurface.Panel;

    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    /// <summary>Шапка страницы: цвет обложки сверху, к низу темнеет.</summary>
    public static IBrush Header(Color c)
    {
        // В светлой теме текст шапки тёмный, поэтому цвет обложки разбавляем белым.
        if (HaPlayer.Ui.ThemeService.IsLight) c = Mix(c, Panel, 0.55);
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(c, 0), new GradientStop(Mix(c, Panel, 0.55), 1) },
        };
    }

    /// <summary>Под шапкой: остаток цвета плавно уходит в фон панели за 240 px.</summary>
    public static IBrush Content(Color c) => ContentBrush(HaPlayer.Ui.ThemeService.IsLight ? Mix(c, Panel, 0.55) : c);

    private static IBrush ContentBrush(Color c) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
        EndPoint = new RelativePoint(0, 240, RelativeUnit.Absolute),
        GradientStops = { new GradientStop(Mix(c, Panel, 0.75), 0), new GradientStop(Panel, 1) },
    };

    /// <summary>Фон экрана «Сейчас играет»: цвет обложки сверху уходит в почти чёрный.</summary>
    public static IBrush NowPlaying(Color c) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Mix(c, Colors.Black, 0.15), 0),
            new GradientStop(Mix(c, Colors.Black, 0.6), 0.55),
            new GradientStop(Color.Parse("#0A0A0A"), 1),
        },
    };
}

public sealed class HomePageViewModel : PageViewModel
{
    public HomePageViewModel(MainViewModel main) : base(main)
    {
        Recent = main.RecentAlbums.Where(a => !main.IsHiddenFromHome(a)
            && !main.Settings.HiddenFromHome.Contains("artist:" + ArtistItem.MakeKey(a.Artist))).Take(30).ToList();
        Smart = main.SmartPlaylists.Where(s => s.Tracks.Count > 0).ToList();
        Quick = Recent.Take(7).Cast<CollectionItem>().Prepend(main.Liked).ToList();
        Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(0, 320, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(HaPlayer.Ui.ThemeService.HeaderTint, 0), new GradientStop(Gradients.Panel, 1) },
        };
    }

    public bool HasMusic => Main.Library.Count > 0;
    public string Greeting => DateTime.Now.Hour switch
    {
        >= 5 and < 12 => L.T("Доброе утро"),
        >= 12 and < 18 => L.T("Добрый день"),
        >= 18 and < 23 => L.T("Добрый вечер"),
        _ => L.T("Доброй ночи"),
    };
    public List<CollectionItem> Quick { get; }
    public List<AlbumItem> Recent { get; }
    public List<SmartItem> Smart { get; }
    public bool HasSmart => Smart.Count > 0;

    private int RowOf(string section)
    {
        var order = Main.Settings.HomeOrder;
        var i = order.IndexOf(section);
        return (i < 0 ? order.Count : i) + 1;
    }

    private bool Shown(string section) => !Main.Settings.HomeHidden.Contains(section);

    public int QuickRow => RowOf("quick");
    public int SmartRow => RowOf("smart");
    public int RecentRow => RowOf("recent");
    public bool ShowQuick => Shown("quick");
    public bool ShowSmart => Shown("smart") && HasSmart;
    public bool ShowRecent => Shown("recent");
    public IBrush Background { get; }
}

public sealed class TracksPageViewModel : PageViewModel
{
    public TracksPageViewModel(MainViewModel main, string filter = "") : base(main)
    {
        Filter = filter;
        IReadOnlyList<TrackItem> list = filter switch
        {
            "lossless" => main.Library.Where(t => t.Model.IsLossless).ToList(),
            "new" => main.Library.Where(t => t.Model.Year >= 2020).ToList(),
            "rated" => main.Library.Where(t => t.Rating >= 4).ToList(),
            "missing" => main.Library.Where(t => t.IsMissing).ToList(),
            "today" => main.Library.Where(t => t.Model.AddedTicks >= DateTime.Today.ToUniversalTime().Ticks).ToList(),
            "week" => main.Library.Where(t => t.Model.AddedTicks >= DateTime.Today.AddDays(-6).ToUniversalTime().Ticks).ToList(),
            _ => main.Library,
        };
        Rows = main.MakeRows(list, L.T("Все треки"), (_, i) => (i + 1).ToString(), t => t.AlbumName, true);
        var total = list.Sum(t => t.Model.DurationSeconds);
        Summary = $"{Format.Tracks(list.Count)}, {Format.Total(total)}";
        SetFilterCommand = new RelayCommand<string>(f => main.ShowTracks(f == Filter ? "" : f ?? ""));
        RemoveMissingCommand = new AsyncRelayCommand(main.RemoveMissingAsync);
        PlayAllCommand = new RelayCommand(() => main.PlayOrToggle(list, 0, L.T("Все треки")));
        ShufflePlayCommand = new RelayCommand(() =>
        {
            if (list.Count == 0) return;
            main.Player.Shuffle = true;
            main.Player.PlayList(list, Random.Shared.Next(list.Count), L.T("Все треки"));
        });
        HeaderBrush = Gradients.Header(HaPlayer.Ui.ThemeService.Mix(HaPlayer.Ui.ThemeService.Current.Accent, Colors.Black, 0.5));
    }

    public bool HasMusic => Main.Library.Count > 0;

    /// <summary>Быстрый фильтр: lossless, new (2020+), rated (4–5 звёзд), missing (нет файла). Пусто — все треки.</summary>
    public string Filter { get; }
    public bool IsLossless => Filter == "lossless";
    public bool IsNew => Filter == "new";
    public bool IsRated => Filter == "rated";
    public bool IsMissing => Filter == "missing";
    public bool IsToday => Filter == "today";
    public bool IsWeek => Filter == "week";
    public bool ShowRatedChip => Main.Settings.ShowRatings;
    public bool ShowMissingChip => Main.MissingCount > 0 || IsMissing;
    public bool CanRemoveMissing => IsMissing && Rows.Count > 0;
    public IRelayCommand<string> SetFilterCommand { get; }
    public IAsyncRelayCommand RemoveMissingCommand { get; }
    public List<TrackRow> Rows { get; }
    public string Summary { get; }
    public IBrush HeaderBrush { get; }
    public IRelayCommand PlayAllCommand { get; }
    public IRelayCommand ShufflePlayCommand { get; }
}

/// <summary>Поиск по названию, исполнителю, альбому и имени файла. Все слова запроса должны найтись.</summary>
public sealed partial class SearchPageViewModel : PageViewModel
{
    private readonly DispatcherTimer _debounce;

    public SearchPageViewModel(MainViewModel main) : base(main)
    {
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) =>
        {
            _debounce!.Stop();
            Run();
        });
        Query = main.SearchQuery;

        // Фильтры: папка медиатеки, год и формат. Первый пункт каждого списка — «без фильтра».
        Folders = [L.T("Все папки"), .. main.Settings.MusicFolders.Select(f => System.IO.Path.GetFileName(f.TrimEnd('\\')) is { Length: > 0 } n ? n : f)];
        _folderPaths = [.. main.Settings.MusicFolders];
        Years = [L.T("Любой год"), .. main.Library.Select(t => t.Model.Year).Where(y => y > 0).Distinct().OrderDescending().Select(y => y.ToString())];
        Formats = [L.T("Любой формат"), .. main.Library.Select(t => t.Extension).Where(e => e.Length > 0).Distinct().Order()];
        Run();
    }

    private readonly List<string> _folderPaths;
    public string NothingFoundText => L.F("По запросу «{0}» ничего нет", Query);
    public List<string> Folders { get; }
    public List<string> Years { get; }
    public List<string> Formats { get; }
    public bool HasFolderFilter => Folders.Count > 2;

    [ObservableProperty] private int _folderIndex;
    [ObservableProperty] private int _yearIndex;
    [ObservableProperty] private int _formatIndex;

    partial void OnFolderIndexChanged(int value) => Run();
    partial void OnYearIndexChanged(int value) => Run();
    partial void OnFormatIndexChanged(int value) => Run();

    public bool HasFilter => FolderIndex > 0 || YearIndex > 0 || FormatIndex > 0;

    [RelayCommand]
    private void ClearFilters()
    {
        FolderIndex = 0;
        YearIndex = 0;
        FormatIndex = 0;
    }

    private bool PassesFilters(TrackItem t)
    {
        if (FolderIndex > 0 && FolderIndex - 1 < _folderPaths.Count)
        {
            var prefix = _folderPaths[FolderIndex - 1].TrimEnd('\\') + "\\";
            if (!t.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (YearIndex > 0 && YearIndex < Years.Count && t.Model.Year.ToString() != Years[YearIndex]) return false;
        if (FormatIndex > 0 && FormatIndex < Formats.Count && t.Extension != Formats[FormatIndex]) return false;
        return true;
    }

    /// <summary>Запрос приходит из поля поиска в верхней панели окна.</summary>
    public string Query { get; private set; }

    public void SetQuery(string value)
    {
        Query = value;
        OnPropertyChanged(nameof(Query));
        OnPropertyChanged(nameof(NothingFoundText));
        _debounce.Stop();
        _debounce.Start();
    }

    [ObservableProperty] private List<TrackRow> _rows = [];
    [ObservableProperty] private List<AlbumItem> _albums = [];
    [ObservableProperty] private List<ArtistItem> _artists = [];
    [ObservableProperty] private List<CollectionItem> _playlists = [];
    [ObservableProperty] private bool _hasQuery;
    [ObservableProperty] private bool _nothingFound;
    [ObservableProperty] private string _tracksTitle = L.T("Треки");

    public bool HasRows => Rows.Count > 0;
    public bool HasAlbums => Albums.Count > 0;
    public bool HasArtists => Artists.Count > 0;
    public bool HasPlaylists => Playlists.Count > 0;

    /// <summary>Подсказка до ввода: альбомы для быстрого старта.</summary>
    public List<AlbumItem> Browse => Main.RecentAlbums.Take(12).ToList();

    private void Run()
    {
        var words = (Query ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        HasQuery = words.Length > 0 || HasFilter;
        OnPropertyChanged(nameof(HasFilter));
        if (!HasQuery)
        {
            Rows = [];
            Albums = [];
            Artists = [];
            Playlists = [];
            NothingFound = false;
            NotifyCounts();
            return;
        }

        bool Match(string text) => words.All(text.Contains);

        var tracks = Main.Library.Where(t => Match(t.SearchText) && PassesFilters(t)).ToList();
        // С фильтром без слов показываем больше: это способ пролистать, например, весь FLAC за 1999 год.
        var limit = words.Length == 0 ? 500 : 100;
        var shown = tracks.Take(limit).ToList();
        Rows = Main.MakeRows(shown, words.Length > 0 ? L.F("Поиск: {0}", Query) : L.T("Поиск"), (_, i) => (i + 1).ToString(), t => t.AlbumName, true);
        TracksTitle = tracks.Count > shown.Count ? L.F("Треки (первые {0} из {1})", shown.Count, tracks.Count) : L.F("Треки ({0})", tracks.Count);

        // Альбомы, исполнители и плейлисты ищутся только по словам: фильтры относятся к трекам.
        var byWords = words.Length > 0 && !HasFilter;
        Albums = byWords ? Main.Albums.Where(a => Match($"{a.Name} {a.Artist}".ToLowerInvariant())).Take(12).ToList() : [];
        Artists = byWords ? Main.Artists.Where(a => Match(a.Name.ToLowerInvariant())).Take(12).ToList() : [];
        Playlists = byWords ? Main.AllPlaylists.Where(p => Match(p.Name.ToLowerInvariant())).Take(12).ToList() : [];
        NothingFound = Rows.Count == 0 && Albums.Count == 0 && Artists.Count == 0 && Playlists.Count == 0;
        NotifyCounts();
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasAlbums));
        OnPropertyChanged(nameof(HasArtists));
        OnPropertyChanged(nameof(HasPlaylists));
    }
}

/// <summary>Страница альбома, исполнителя, плейлиста или «Любимых треков». Разметка одна, отличаются подписи и кнопки.</summary>
public sealed partial class CollectionPageViewModel : PageViewModel
{
    private static readonly Color Fallback = Color.Parse("#535353");
    private static Color LikedColor => HaPlayer.Ui.ThemeService.Mix(HaPlayer.Ui.ThemeService.Current.Deep, HaPlayer.Ui.ThemeService.Current.Accent, 0.3);

    public CollectionPageViewModel(MainViewModel main, CollectionItem item) : base(main)
    {
        Item = item;
        var list = item.Tracks;
        var playList = item is FolderItem folder ? folder.AllTracks : list;
        var isAlbum = item is AlbumItem;

        Rows = main.MakeRows(list, item.Name,
            (t, i) => isAlbum && t.Model.TrackNo > 0 ? t.Model.TrackNo.ToString() : (i + 1).ToString(),
            t => isAlbum ? "" : t.AlbumName,
            !isAlbum,
            item is PlaylistItem pl ? t => main.IsPinnedIn(pl, t) : null);
        Note = item is AlbumItem ? main.Note("album", item.Key) : "";

        var total = list.Sum(t => t.Model.DurationSeconds);
        var stats = list.Count > 0 ? $"{Format.Tracks(list.Count)}, {Format.Total(total)}" : L.T("пока пусто");
        (KindLabel, Owner, Meta) = item switch
        {
            AlbumItem a => (L.T("Альбом"), a.Artist, (a.Year > 0 ? $" • {a.Year}" : "") + $" • {stats}"),
            ArtistItem => (L.T("Исполнитель"), "", stats),
            SmartItem s => (L.T("Подборка"), s.Description, " • " + stats),
            FolderItem f => (L.T("Папка"), "", " • " + L.F("{0} внутри", Format.Tracks(f.AllTracks.Count))),
            _ => (L.T("Плейлист"), "", stats),
        };

        PlayCommand = new RelayCommand(() => main.Play(item));
        ExportCommand = new AsyncRelayCommand(() => main.ExportM3uAsync(item));
        ShufflePlayCommand = new RelayCommand(() =>
        {
            if (playList.Count == 0) return;
            main.Player.Shuffle = true;
            main.Player.PlayList(playList, Random.Shared.Next(playList.Count), item.Name);
        });
        QueueCommand = new RelayCommand(() => main.Player.AddToQueue(playList));
        LikeAllCommand = new RelayCommand(() =>
        {
            // Всё уже в любимых — убираем, иначе добавляем недостающие.
            main.ToggleLike(AllLiked ? list : list.Where(t => !t.IsLiked).ToList());
            OnPropertyChanged(nameof(AllLiked));
        });
        if (item is ArtistItem) ArtistTab = OwnAlbums.Count > 0 ? 0 : 1;
        AddSongsCommand = new AsyncRelayCommand(() => main.AddSongsFromDiskAsync(item));
        ChangePhotoCommand = new AsyncRelayCommand(() => main.ChangePhotoAsync(item));
        RenameCommand = new AsyncRelayCommand(() => item is PlaylistItem p ? main.RenamePlaylistAsync(p) : Task.CompletedTask);
        DeleteCommand = new AsyncRelayCommand(() => item is PlaylistItem p ? main.DeletePlaylistAsync(p) : Task.CompletedTask);

        var start = item.IsLikedCollection ? LikedColor : Fallback;
        _headerBrush = Gradients.Header(start);
        _contentBrush = Gradients.Content(start);
        if (!item.IsLikedCollection && main.Settings.HeaderFromCover) _ = LoadColorAsync();
    }

    private async Task LoadColorAsync()
    {
        if (Item.CoverSource is not { } source) return;
        if (await Main.Covers.GetColorAsync(source) is { } c)
        {
            HeaderBrush = Gradients.Header(c);
            ContentBrush = Gradients.Content(c);
        }
    }

    public CollectionItem Item { get; }
    public override PlaylistItem? Playlist => Item as PlaylistItem;

    /// <summary>Заметка к альбому. Пусто — заметки нет.</summary>
    public string Note { get; }
    public bool HasNote => Note.Length > 0;

    /// <summary>Трек, к которому нужно прокрутить и выделить («Показать в медиатеке»). Страница забирает его один раз.</summary>
    public TrackItem? PendingReveal { get; set; }

    public event Action<TrackItem>? RevealRequested;

    public void Reveal(TrackItem track)
    {
        if (IsArtist) ArtistTab = 1;
        PendingReveal = track;
        RevealRequested?.Invoke(track);
    }

    public string KindLabel { get; }
    public string Owner { get; }
    public string Meta { get; }
    public List<TrackRow> Rows { get; }

    public bool IsArtist => Item is ArtistItem;

    /// <summary>
    /// Все альбомы, где есть исполнитель, от новых к старым. Свои — где он исполнитель альбома
    /// или поёт больше половины треков; остальные попадают в «Появляется в».
    /// </summary>
    private List<AlbumItem> ArtistAlbums => _artistAlbums ??= Item is ArtistItem artist
        ? Main.Albums
            .Where(a => a.Tracks.Any(t => ArtistItem.MakeKey(t.Artist) == artist.Key))
            .OrderByDescending(a => a.Year)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList()
        : [];

    private List<AlbumItem>? _artistAlbums;
    private List<AlbumItem>? _own;
    private List<AlbumItem>? _appears;

    private bool IsOwn(AlbumItem a) =>
        ArtistItem.MakeKey(a.Artist) == Item.Key
        || a.Tracks.Count(t => ArtistItem.MakeKey(t.Artist) == Item.Key) * 2 > a.Tracks.Count;

    public List<AlbumItem> OwnAlbums => _own ??= ArtistAlbums.Where(IsOwn).ToList();
    public List<AlbumItem> AppearsOn => _appears ??= ArtistAlbums.Where(a => !IsOwn(a)).ToList();

    /// <summary>Вкладка страницы исполнителя: 0 альбомы, 1 все треки, 2 «Появляется в».</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOwnAlbums), nameof(ShowAppearsOn), nameof(ShowTrackList))]
    private int _artistTab = 1;

    public bool HasArtistAlbums => OwnAlbums.Count > 0;
    public bool HasAppearsOn => AppearsOn.Count > 0;

    /// <summary>Вложенные папки на странице папки.</summary>
    public List<FolderItem> SubFolders => _subFolders ??= Item is FolderItem f ? Main.SubFolders(f) : [];
    private List<FolderItem>? _subFolders;
    public bool HasSubFolders => SubFolders.Count > 0;
    public bool ShowOwnAlbums => IsArtist && ArtistTab == 0;
    public bool ShowAppearsOn => IsArtist && ArtistTab == 2;
    public bool ShowTrackList => HasTracks && (!IsArtist || ArtistTab == 1);
    public List<AlbumItem> TabAlbums => ArtistTab == 2 ? AppearsOn : OwnAlbums;

    [RelayCommand]
    private void SelectArtistTab(string tab)
    {
        ArtistTab = int.Parse(tab);
        OnPropertyChanged(nameof(TabAlbums));
    }

    public bool AllLiked => Item.Tracks.Count > 0 && Item.Tracks.All(t => t.IsLiked);
    public bool CanLikeAll => !Item.IsLikedCollection && HasTracks;
    public IRelayCommand QueueCommand { get; }
    public IRelayCommand LikeAllCommand { get; }
    public bool IsPlaylist => Item is PlaylistItem;
    public bool CanChangePhoto => !Item.IsLikedCollection && Item is not (SmartItem or FolderItem);

    /// <summary>«Добавить» у альбома и исполнителя меняет привязку, поэтому в строгом режиме скрыта.</summary>
    public bool CanAddSongs => Item switch
    {
        SmartItem or FolderItem => false,
        AlbumItem or ArtistItem => !Main.Settings.StrictTags,
        _ => true,
    };

    public IAsyncRelayCommand ExportCommand { get; private set; } = null!;
    public bool HasTracks => Rows.Count > 0;
    public bool ShowAlbumColumn => Item is not AlbumItem;
    public CornerRadius CoverRadius => IsArtist ? new CornerRadius(116) : HaPlayer.Ui.ThemeService.Radius(4);

    public string EmptyText => Item switch
    {
        LikedItem => L.T("Отмечайте песни кружком с плюсом или клавишей L."),
        PlaylistItem => L.T("Пусто. Добавьте песни кнопкой «Добавить» или правым кликом по треку."),
        SmartItem { Key: "recent" } => L.T("Пусто."),
        SmartItem => L.T("Здесь появятся треки, когда наберётся история прослушивания."),
        _ => L.T("Пусто."),
    };

    public IRelayCommand PlayCommand { get; }
    public IRelayCommand ShufflePlayCommand { get; }
    public string MoreTip => L.F("Другие действия: {0}", Item.Name);
    public IAsyncRelayCommand AddSongsCommand { get; }
    public IAsyncRelayCommand ChangePhotoCommand { get; }
    public IAsyncRelayCommand RenameCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }

    [ObservableProperty] private IBrush _headerBrush;
    [ObservableProperty] private IBrush _contentBrush;
}
