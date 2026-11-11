using HaPlayer.Ui;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlayer.Library;

namespace HaPlayer.ViewModels;

/// <summary>Трек в медиатеке. Один экземпляр на файл, общий для всех списков, поэтому подсветка текущего трека видна везде.</summary>
public sealed partial class TrackItem(Track model) : ObservableObject
{
    public Track Model { get; private set; } = model;
    public string Path => Model.Path;
    public string Title => Model.Title;
    public string Artist => L.Data(Model.EffectiveArtist);
    public string AlbumName => L.Data(Model.EffectiveAlbum);
    public string DurationText => Model.DurationSeconds > 0 ? Format.Time(Model.DurationSeconds) : "–";

    private static System.Globalization.CultureInfo Ru => L.Culture;

    /// <summary>«4 сент. 2026 г.», как в колонке «Дата добавления» у Spotify.</summary>
    public string AddedText => Model.AddedTicks > 0
        ? new DateTime(Model.AddedTicks, DateTimeKind.Utc).ToLocalTime().ToString("d MMM yyyy", Ru)
        : "";

    [ObservableProperty] private AlbumItem? _album;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isLiked;

    /// <summary>Файла нет на месте: диск отключён, файл удалён или переименован.</summary>
    [ObservableProperty] private bool _isMissing;

    /// <summary>Сколько раз трек дослушали (по истории) и когда в последний раз, UTC ticks.</summary>
    public int PlayCount { get; set; }
    public long LastPlayedTicks { get; set; }

    public string Extension => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();

    /// <summary>«FLAC · 44,1 кГц · 912 кбит/с · стерео». Пусто, пока данные не прочитаны.</summary>
    public string TechText
    {
        get
        {
            var m = Model;
            if (m.SampleRate <= 0) return Extension;
            var parts = new List<string> { Extension, L.F("{0:0.#} кГц", m.SampleRate / 1000.0) };
            if (m.Bitrate > 0) parts.Add(L.F("{0} кбит/с", m.Bitrate));
            parts.Add(m.Channels switch { 1 => L.T("моно"), 2 => L.T("стерео"), > 2 => L.F("{0} кан.", m.Channels), _ => "" });
            return string.Join(" · ", parts.Where(p => p.Length > 0));
        }
    }

    /// <summary>
    /// Одна и та же песня в разных файлах (копия в другой папке, другой битрейт) даёт один ключ:
    /// название, исполнитель и длительность с точностью до 3 секунд.
    /// </summary>
    public string SongKey =>
        $"{Title.Trim().ToLowerInvariant()}\u001f{Artist.Trim().ToLowerInvariant()}\u001f{(int)Math.Round(Model.DurationSeconds / 3)}";

    private string? _searchText;

    /// <summary>Строка для поиска: название, исполнитель, альбом и имя файла в нижнем регистре.</summary>
    public string SearchText => _searchText ??=
        $"{Title} {Artist} {AlbumName} {System.IO.Path.GetFileNameWithoutExtension(Path)}".ToLowerInvariant();

    /// <summary>Подменяет данные после пересканирования, сохраняя тот же объект (на него ссылаются очередь и плеер).</summary>
    public void Update(Track model)
    {
        Model = model;
        Refresh();
    }

    public void Refresh()
    {
        _searchText = null;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>Альбом, исполнитель или плейлист: что-то с названием, обложкой и списком треков.</summary>
public abstract partial class CollectionItem : ObservableObject
{
    private readonly CoverService _covers;
    private Bitmap? _thumb;
    private Bitmap? _art;
    private bool _thumbRequested;
    private bool _artRequested;

    protected CollectionItem(CoverService covers, string? customImage, Action<CollectionItem> open, Action<CollectionItem> play)
    {
        _covers = covers;
        CustomImage = customImage;
        OpenCommand = new RelayCommand(() => open(this));
        PlayCommand = new RelayCommand(() => play(this));
    }

    public abstract string Kind { get; }
    public abstract string Key { get; }
    public abstract string Name { get; }
    public abstract List<TrackItem> Tracks { get; }
    public abstract string Subtitle { get; }

    /// <summary>Фото, которое поставил пользователь. Если его нет, берётся обложка первого трека.</summary>
    public string? CustomImage { get; }

    /// <summary>«Любимые треки» рисуются своим градиентом с сердцем вместо обложки.</summary>
    public virtual bool IsLikedCollection => false;
    public bool HasCover => !IsLikedCollection;

    /// <summary>Значок вместо обложки для особых коллекций («Любимые треки», «Все треки»).</summary>
    public virtual Avalonia.Media.Geometry? Badge => null;

    /// <summary>Закреплено всегда («Любимые треки», «Все треки»): открепить нельзя.</summary>
    public virtual bool IsBuiltInPinned => false;

    /// <summary>Закрепил пользователь через «…» или правый клик.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPinned))]
    private bool _isUserPinned;

    /// <summary>Закреплено сверху медиатеки: показываем булавку.</summary>
    public bool IsPinned => IsBuiltInPinned || IsUserPinned;

    /// <summary>Как называть коллекцию в вопросах «Удалить …?».</summary>
    public string KindName => this switch
    {
        AlbumItem => L.T("альбом"),
        ArtistItem => L.T("исполнителя"),
        _ => L.T("плейлист"),
    };

    /// <summary>Исполнители в медиатеке с круглым фото.</summary>
    public bool IsArtist => this is ArtistItem;
    public Avalonia.CornerRadius ListCornerRadius => IsArtist ? new Avalonia.CornerRadius(24) : new Avalonia.CornerRadius(4);

    /// <summary>Из этой коллекции сейчас играет очередь: название подсвечивается акцентом.</summary>
    [ObservableProperty] private bool _isNowPlaying;

    public string? CoverSource => CustomImage ?? (Tracks.Count > 0 ? Tracks[0].Path : null);

    public IRelayCommand OpenCommand { get; }
    public IRelayCommand PlayCommand { get; }

    // Обложки грузятся лениво, при первом чтении свойства биндингом.
    // Списки виртуализированы, поэтому грузится только то, что видно на экране.
    public Bitmap? Thumb
    {
        get
        {
            if (!_thumbRequested)
            {
                _thumbRequested = true;
                _ = LoadAsync(96, b => { _thumb = b; OnPropertyChanged(nameof(Thumb)); });
            }
            return _thumb;
        }
    }

    public Bitmap? Art
    {
        get
        {
            if (!_artRequested)
            {
                _artRequested = true;
                _ = LoadAsync(400, b => { _art = b; OnPropertyChanged(nameof(Art)); });
            }
            return _art;
        }
    }

    private async Task LoadAsync(int width, Action<Bitmap> apply)
    {
        if (CoverSource is not { } source) return;
        var bmp = await _covers.GetAsync(source, width);
        if (bmp != null) apply(bmp);
    }
}

public sealed class AlbumItem(
    string key, string name, string artist, int year, List<TrackItem> tracks,
    CoverService covers, string? customImage, Action<CollectionItem> open, Action<CollectionItem> play)
    : CollectionItem(covers, customImage, open, play)
{
    public override string Kind => "album";
    public override string Key { get; } = key;
    public override string Name { get; } = name;
    public override List<TrackItem> Tracks { get; } = tracks;
    public string Artist { get; } = artist;
    public int Year { get; } = year;

    public override string Subtitle => L.F("Альбом • {0}", Artist);
    public string CardSubtitle => Year > 0 ? $"{Year} • {Artist}" : Artist;
}

public sealed class ArtistItem(
    string name, List<TrackItem> tracks,
    CoverService covers, string? customImage, Action<CollectionItem> open, Action<CollectionItem> play)
    : CollectionItem(covers, customImage, open, play)
{
    public static string MakeKey(string name) => name.Trim().ToLowerInvariant();

    public override string Kind => "artist";
    public override string Key { get; } = MakeKey(name);
    public override string Name { get; } = name;
    public override List<TrackItem> Tracks { get; } = tracks;
    public override string Subtitle => L.T("Исполнитель");
}

public sealed class PlaylistItem(
    long id, string name, List<TrackItem> tracks,
    CoverService covers, string? customImage, Action<CollectionItem> open, Action<CollectionItem> play)
    : CollectionItem(covers, customImage, open, play)
{
    public long Id { get; } = id;
    public override string Kind => "playlist";
    public override string Key { get; } = id.ToString();
    public override string Name { get; } = name;
    public override List<TrackItem> Tracks { get; } = tracks;
    public override string Subtitle => L.F("Плейлист • {0}", Format.Tracks(Tracks.Count));
}

/// <summary>Вся медиатека одним списком. Закреплена в медиатеке слева и открывает страницу «Все треки».</summary>
public sealed class AllTracksItem(List<TrackItem> tracks, CoverService covers, Action<CollectionItem> open, Action<CollectionItem> play)
    : CollectionItem(covers, null, open, play)
{
    public override string Kind => "all";
    public override string Key => "all";
    public override string Name => L.T("Все треки");
    public override List<TrackItem> Tracks { get; } = tracks;
    public override string Subtitle => L.F("Медиатека • {0}", Format.Tracks(Tracks.Count));
    public override Avalonia.Media.Geometry? Badge => HaPlayer.Ui.Icons.Library;
    public override bool IsBuiltInPinned => true;
}

/// <summary>
/// Подборка, которую плеер собирает сам: недавно добавленные, часто слушаю, давно не слушал.
/// Список пересчитывается при каждом открытии, вручную его не меняют.
/// </summary>
public sealed class SmartItem(string key, string name, string description, List<TrackItem> tracks,
    CoverService covers, Action<CollectionItem> open, Action<CollectionItem> play)
    : CollectionItem(covers, null, open, play)
{
    public override string Kind => "smart";
    public override string Key { get; } = key;
    public override string Name { get; } = name;
    public override List<TrackItem> Tracks { get; } = tracks;
    public string Description { get; } = description;
    public override string Subtitle => L.F("Подборка • {0}", Format.Tracks(Tracks.Count));
    public string CardSubtitle => Description;
    public override Avalonia.Media.Geometry? Badge => HaPlayer.Ui.Icons.Sparkles;
}

/// <summary>Строка истории прослушивания: трек и когда его слушали.</summary>
public sealed class HistoryItem(TrackItem track, DateTime playedAtLocal, Action play)
{
    public TrackItem Track { get; } = track;
    public IRelayCommand PlayCommand { get; } = new RelayCommand(play);

    public string When
    {
        get
        {
            var ago = DateTime.Now - playedAtLocal;
            if (ago.TotalMinutes < 1) return L.T("только что");
            if (ago.TotalMinutes < 60) return L.F("{0} мин назад", (int)ago.TotalMinutes);
            if (playedAtLocal.Date == DateTime.Today) return L.F("сегодня в {0:HH:mm}", playedAtLocal);
            if (playedAtLocal.Date == DateTime.Today.AddDays(-1)) return L.F("вчера в {0:HH:mm}", playedAtLocal);
            return playedAtLocal.ToString("d MMM, HH:mm", L.Culture);
        }
    }
}

/// <summary>Устройство вывода в панели «Устройства».</summary>
public sealed class DeviceItem(HaPlayer.Audio.AudioDevice device, bool isCurrent, Action select)
{
    public string Name { get; } = device.Name;
    public bool IsCurrent { get; } = isCurrent;
    public string Subtitle => device.IsDefault ? L.T("Устройство Windows по умолчанию") : device.Kind switch
    {
        HaPlayer.Audio.DeviceKind.Headphones => L.T("Наушники"),
        HaPlayer.Audio.DeviceKind.Bluetooth => "Bluetooth",
        HaPlayer.Audio.DeviceKind.Monitor => L.T("Монитор или телевизор"),
        _ => L.T("Колонки"),
    };
    public Avalonia.Media.Geometry Icon => device.Kind switch
    {
        HaPlayer.Audio.DeviceKind.Headphones => HaPlayer.Ui.Icons.Headphones,
        HaPlayer.Audio.DeviceKind.Bluetooth => HaPlayer.Ui.Icons.Bluetooth,
        HaPlayer.Audio.DeviceKind.Monitor => HaPlayer.Ui.Icons.Monitor,
        _ => HaPlayer.Ui.Icons.Speaker,
    };
    public IRelayCommand SelectCommand { get; } = new RelayCommand(select);
}

/// <summary>Трек в списке «Далее в очереди» на экране «Сейчас играет».</summary>
public sealed class UpNextItem(TrackItem track, bool queued, Action play)
{
    public TrackItem Track { get; } = track;

    /// <summary>Поставлен вручную через «Играть следующим» или «В очередь».</summary>
    public bool Queued { get; } = queued;
    public IRelayCommand PlayCommand { get; } = new RelayCommand(play);
}

/// <summary>Сердечко у трека собирает плейлист «Любимые треки», новые сверху.</summary>
public sealed class LikedItem(List<TrackItem> tracks, CoverService covers, Action<CollectionItem> open, Action<CollectionItem> play)
    : CollectionItem(covers, null, open, play)
{
    public override string Kind => "liked";
    public override string Key => "liked";
    public override string Name => L.T("Любимые треки");
    public override List<TrackItem> Tracks { get; } = tracks;
    public override string Subtitle => L.F("Плейлист • {0}", Format.Tracks(Tracks.Count));
    public override bool IsLikedCollection => true;
    public override Avalonia.Media.Geometry? Badge => HaPlayer.Ui.Icons.Heart;
    public override bool IsBuiltInPinned => true;
}

/// <summary>Строка в таблице треков: номер зависит от списка, поэтому хранится отдельно от TrackItem.</summary>
public sealed class TrackRow(int index, string number, TrackItem item, string secondary, bool showCover,
    Action play, Action toggle, Action like, TrackRowOptions? options = null)
{
    private readonly TrackRowOptions _o = options ?? TrackRowOptions.Default;

    public int Index { get; } = index;
    public string Number { get; } = number;
    public TrackItem Item { get; } = item;
    public string Secondary { get; } = secondary;
    public bool ShowCover { get; } = showCover && !(options?.Compact ?? false);
    public bool Compact => _o.Compact;
    public double RowHeight => _o.Compact ? 40 : 56;
    public double LineSpacing => _o.Compact ? 0 : 3;

    /// <summary>Колонка с техданными и числом прослушиваний: только если включено в настройках.</summary>
    public string Extra
    {
        get
        {
            var parts = new List<string>(2);
            if (_o.ShowTech) parts.Add(Item.TechText);
            if (_o.ShowPlays && Item.PlayCount > 0) parts.Add($"{Item.PlayCount} ×");
            return string.Join("  ", parts);
        }
    }

    public bool HasExtra => _o.ShowTech || _o.ShowPlays;

    /// <summary>Двойной клик: играть с этого трека.</summary>
    public IRelayCommand PlayCommand { get; } = new RelayCommand(play);

    /// <summary>Кнопка в строке: играть, а если этот трек уже играет, пауза.</summary>
    public IRelayCommand ToggleCommand { get; } = new RelayCommand(toggle);

    public IRelayCommand LikeCommand { get; } = new RelayCommand(like);
}

/// <summary>Вид строк в списках треков, из настроек.</summary>
public sealed record TrackRowOptions(bool Compact, bool ShowTech, bool ShowPlays)
{
    public static readonly TrackRowOptions Default = new(false, false, false);
}

public static class Format
{
    public static string Time(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string Total(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? L.F("{0} ч {1} мин", (int)t.TotalHours, t.Minutes) : L.F("{0} мин", t.Minutes);
    }

    public static string Tracks(int n) => L.Plural(n, "трек", "трека", "треков", "track", "tracks");
}
