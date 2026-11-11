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
        Recent = main.RecentAlbums.Take(30).ToList();
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
    public IBrush Background { get; }
}

public sealed class TracksPageViewModel : PageViewModel
{
    public TracksPageViewModel(MainViewModel main) : base(main)
    {
        var list = main.Library;
        Rows = main.MakeRows(list, L.T("Все треки"), (_, i) => (i + 1).ToString(), t => t.AlbumName, true);
        var total = list.Sum(t => t.Model.DurationSeconds);
        Summary = $"{Format.Tracks(list.Count)}, {Format.Total(total)}";
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
        var isAlbum = item is AlbumItem;

        Rows = main.MakeRows(list, item.Name,
            (t, i) => isAlbum && t.Model.TrackNo > 0 ? t.Model.TrackNo.ToString() : (i + 1).ToString(),
            t => isAlbum ? "" : t.AlbumName,
            !isAlbum);

        var total = list.Sum(t => t.Model.DurationSeconds);
        var stats = list.Count > 0 ? $"{Format.Tracks(list.Count)}, {Format.Total(total)}" : "пока пусто";
        (KindLabel, Owner, Meta) = item switch
        {
            AlbumItem a => (L.T("Альбом"), a.Artist, (a.Year > 0 ? $" • {a.Year}" : "") + $" • {stats}"),
            ArtistItem => (L.T("Исполнитель"), "", stats),
            SmartItem s => (L.T("Подборка"), s.Description, " • " + stats),
            _ => (L.T("Плейлист"), "", stats),
        };

        PlayCommand = new RelayCommand(() => main.Play(item));
        ExportCommand = new AsyncRelayCommand(() => main.ExportM3uAsync(item));
        ShufflePlayCommand = new RelayCommand(() =>
        {
            if (list.Count == 0) return;
            main.Player.Shuffle = true;
            main.Player.PlayList(list, Random.Shared.Next(list.Count), item.Name);
        });
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

    public string KindLabel { get; }
    public string Owner { get; }
    public string Meta { get; }
    public List<TrackRow> Rows { get; }

    public bool IsArtist => Item is ArtistItem;

    /// <summary>
    /// Альбомы исполнителя для страницы исполнителя: те, где поёт он сам, от новых к старым.
    /// Сборники, где у него один трек, тоже попадают: так видно, где ещё он есть.
    /// </summary>
    public List<AlbumItem> ArtistAlbums => _artistAlbums ??= Item is ArtistItem artist
        ? Main.Albums
            .Where(a => a.Tracks.Any(t => ArtistItem.MakeKey(t.Artist) == artist.Key))
            .OrderByDescending(a => a.Year)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList()
        : [];

    private List<AlbumItem>? _artistAlbums;

    public bool HasArtistAlbums => ArtistAlbums.Count > 0;
    public bool IsPlaylist => Item is PlaylistItem;
    public bool CanChangePhoto => !Item.IsLikedCollection && Item is not SmartItem;

    /// <summary>«Добавить» у альбома и исполнителя меняет привязку, поэтому в строгом режиме скрыта.</summary>
    public bool CanAddSongs => Item switch
    {
        SmartItem => false,
        AlbumItem or ArtistItem => !Main.Settings.StrictTags,
        _ => true,
    };

    public IAsyncRelayCommand ExportCommand { get; private set; } = null!;
    public bool HasTracks => Rows.Count > 0;
    public bool ShowAlbumColumn => Item is not AlbumItem;
    public CornerRadius CoverRadius => IsArtist ? new CornerRadius(116) : new CornerRadius(4);

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
