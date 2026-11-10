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
    public static IBrush Header(Color c) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(c, 0), new GradientStop(Mix(c, Panel, 0.55), 1) },
    };

    /// <summary>Под шапкой: остаток цвета плавно уходит в фон панели за 240 px.</summary>
    public static IBrush Content(Color c) => new LinearGradientBrush
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
        >= 5 and < 12 => "Доброе утро",
        >= 12 and < 18 => "Добрый день",
        >= 18 and < 23 => "Добрый вечер",
        _ => "Доброй ночи",
    };
    public List<CollectionItem> Quick { get; }
    public List<AlbumItem> Recent { get; }
    public IBrush Background { get; }
}

public sealed class TracksPageViewModel : PageViewModel
{
    public TracksPageViewModel(MainViewModel main) : base(main)
    {
        var list = main.Library;
        Rows = main.MakeRows(list, "Все треки", (_, i) => (i + 1).ToString(), t => t.AlbumName, true);
        var total = list.Sum(t => t.Model.DurationSeconds);
        Summary = $"{Format.Tracks(list.Count)}, {Format.Total(total)}";
        PlayAllCommand = new RelayCommand(() => main.PlayOrToggle(list, 0, "Все треки"));
        ShufflePlayCommand = new RelayCommand(() =>
        {
            if (list.Count == 0) return;
            main.Player.Shuffle = true;
            main.Player.PlayList(list, Random.Shared.Next(list.Count), "Все треки");
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
        Run();
    }

    /// <summary>Запрос приходит из поля поиска в верхней панели окна.</summary>
    public string Query { get; private set; }

    public void SetQuery(string value)
    {
        Query = value;
        OnPropertyChanged(nameof(Query));
        _debounce.Stop();
        _debounce.Start();
    }

    [ObservableProperty] private List<TrackRow> _rows = [];
    [ObservableProperty] private List<AlbumItem> _albums = [];
    [ObservableProperty] private List<ArtistItem> _artists = [];
    [ObservableProperty] private List<CollectionItem> _playlists = [];
    [ObservableProperty] private bool _hasQuery;
    [ObservableProperty] private bool _nothingFound;
    [ObservableProperty] private string _tracksTitle = "Треки";

    public bool HasRows => Rows.Count > 0;
    public bool HasAlbums => Albums.Count > 0;
    public bool HasArtists => Artists.Count > 0;
    public bool HasPlaylists => Playlists.Count > 0;

    /// <summary>Подсказка до ввода: альбомы для быстрого старта.</summary>
    public List<AlbumItem> Browse => Main.RecentAlbums.Take(12).ToList();

    private void Run()
    {
        var words = (Query ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        HasQuery = words.Length > 0;
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

        var tracks = Main.Library.Where(t => Match(t.SearchText)).ToList();
        var shown = tracks.Take(100).ToList();
        Rows = Main.MakeRows(shown, $"Поиск: {Query}", (_, i) => (i + 1).ToString(), t => t.AlbumName, true);
        TracksTitle = tracks.Count > shown.Count ? $"Треки (первые {shown.Count} из {tracks.Count})" : "Треки";

        Albums = Main.Albums.Where(a => Match($"{a.Name} {a.Artist}".ToLowerInvariant())).Take(12).ToList();
        Artists = Main.Artists.Where(a => Match(a.Name.ToLowerInvariant())).Take(12).ToList();
        Playlists = Main.AllPlaylists.Where(p => Match(p.Name.ToLowerInvariant())).Take(12).ToList();
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
            AlbumItem a => ("Альбом", a.Artist, (a.Year > 0 ? $" • {a.Year}" : "") + $" • {stats}"),
            ArtistItem => ("Исполнитель", "", stats),
            _ => ("Плейлист", "", stats),
        };

        PlayCommand = new RelayCommand(() => main.Play(item));
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
    public bool IsPlaylist => Item is PlaylistItem;
    public bool CanChangePhoto => !Item.IsLikedCollection;
    public bool HasTracks => Rows.Count > 0;
    public bool ShowAlbumColumn => Item is not AlbumItem;
    public CornerRadius CoverRadius => IsArtist ? new CornerRadius(116) : new CornerRadius(4);

    public string EmptyText => Item switch
    {
        LikedItem => "Отмечайте песни кружком с плюсом или клавишей L.",
        PlaylistItem => "Пусто. Добавьте песни кнопкой «Добавить» или правым кликом по треку.",
        _ => "Пусто.",
    };

    public IRelayCommand PlayCommand { get; }
    public IRelayCommand ShufflePlayCommand { get; }
    public string MoreTip => $"Другие действия: {Item.Name}";
    public IAsyncRelayCommand AddSongsCommand { get; }
    public IAsyncRelayCommand ChangePhotoCommand { get; }
    public IAsyncRelayCommand RenameCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }

    [ObservableProperty] private IBrush _headerBrush;
    [ObservableProperty] private IBrush _contentBrush;
}
