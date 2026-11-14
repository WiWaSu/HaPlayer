using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlayer.Ui;

namespace HaPlayer.ViewModels;

/// <summary>Строка рейтинга в «Итогах»: место, название, подпись, сколько раз, доля для полоски.</summary>
public sealed record StatsRow(int Place, string Name, string Sub, string Count, double Share, CollectionItem? Open)
{
    public IRelayCommand OpenCommand { get; } = new RelayCommand(() => Open?.OpenCommand.Execute(null));
}

/// <summary>Столбик графика: подпись месяца, высота 0..1, значение для подсказки.</summary>
public sealed record StatsBar(string Label, double Height, string Tip)
{
    public double Pixels => Math.Max(3, Height * 140);
}

/// <summary>
/// «Итоги»: что и сколько слушали за неделю, месяц, год или всё время. Считается по истории прослушивания,
/// которая хранится только на этом компьютере. Без истории страница пустая.
/// </summary>
public sealed partial class StatsPageViewModel : PageViewModel
{
    private readonly List<(TrackItem Track, DateTime At)> _all;

    public StatsPageViewModel(MainViewModel main) : base(main)
    {
        _all = main.LoadAllPlays();
        Build();
    }

    /// <summary>Период: 0 неделя, 1 месяц, 2 год, 3 всё время.</summary>
    [ObservableProperty] private int _period = 1;

    partial void OnPeriodChanged(int value) => Build();

    [RelayCommand]
    private void SelectPeriod(string p) => Period = int.Parse(p);

    public bool IsWeek => Period == 0;
    public bool IsMonth => Period == 1;
    public bool IsYear => Period == 2;
    public bool IsAll => Period == 3;

    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _hoursText = "";
    [ObservableProperty] private string _playsText = "";
    [ObservableProperty] private string _tracksText = "";
    [ObservableProperty] private string _streakText = "";
    [ObservableProperty] private string _bestStreakText = "";
    [ObservableProperty] private string _hourText = "";
    [ObservableProperty] private List<StatsRow> _topArtists = [];
    [ObservableProperty] private List<StatsRow> _topTracks = [];
    [ObservableProperty] private List<StatsRow> _topAlbums = [];
    [ObservableProperty] private List<StatsBar> _months = [];

    public bool HasHistory => _all.Count > 0;
    public bool HistoryOff => !Main.Settings.KeepHistory;

    private void Build()
    {
        OnPropertyChanged(nameof(IsWeek));
        OnPropertyChanged(nameof(IsMonth));
        OnPropertyChanged(nameof(IsYear));
        OnPropertyChanged(nameof(IsAll));

        var from = Period switch
        {
            0 => DateTime.Now.Date.AddDays(-6),
            1 => DateTime.Now.Date.AddDays(-29),
            2 => DateTime.Now.Date.AddDays(-364),
            _ => DateTime.MinValue,
        };
        var plays = _all.Where(p => p.At >= from).ToList();
        HasData = plays.Count > 0;

        var seconds = plays.Sum(p => p.Track.Model.DurationSeconds);
        HoursText = seconds >= 3600 ? L.F("{0:0.#} ч", seconds / 3600) : L.F("{0:0} мин", seconds / 60);
        PlaysText = L.Plural(plays.Count, "прослушивание", "прослушивания", "прослушиваний", "play", "plays");
        TracksText = L.Plural(plays.Select(p => p.Track).Distinct().Count(), "разный трек", "разных трека", "разных треков",
            "different track", "different tracks");

        // Серии: сколько дней подряд что-то звучало. Текущая считается от сегодня или вчера.
        var days = _all.Select(p => p.At.Date).ToHashSet();
        var streak = 0;
        var day = days.Contains(DateTime.Today) ? DateTime.Today : DateTime.Today.AddDays(-1);
        while (days.Contains(day))
        {
            streak++;
            day = day.AddDays(-1);
        }
        var best = 0;
        var run = 0;
        DateTime? prev = null;
        foreach (var d in days.OrderBy(d => d))
        {
            run = prev != null && (d - prev.Value).TotalDays == 1 ? run + 1 : 1;
            best = Math.Max(best, run);
            prev = d;
        }
        StreakText = L.Plural(streak, "день", "дня", "дней", "day", "days");
        BestStreakText = L.Plural(best, "день", "дня", "дней", "day", "days");

        var hour = plays.GroupBy(p => p.At.Hour).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        HourText = hour is { } h ? $"{h:00}:00–{(h + 1) % 24:00}:00" : "–";

        TopArtists = Top(plays.GroupBy(p => p.Track.Artist, StringComparer.OrdinalIgnoreCase),
            g => g.Key, _ => "", g => Main.Artists.FirstOrDefault(a => a.Key == ArtistItem.MakeKey(g.Key)));
        TopTracks = Top(plays.GroupBy(p => p.Track),
            g => g.Key.Title, g => g.Key.Artist, g => g.Key.Album);
        TopAlbums = Top(plays.Where(p => p.Track.Album != null).GroupBy(p => p.Track.Album!),
            g => g.Key.Name, g => g.Key.Artist, g => g.Key);

        // Часы по месяцам за последний год: видно, когда слушали больше.
        var start = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-11);
        var byMonth = Enumerable.Range(0, 12).Select(i => start.AddMonths(i)).Select(m =>
        {
            var s = _all.Where(p => p.At >= m && p.At < m.AddMonths(1)).Sum(p => p.Track.Model.DurationSeconds) / 3600;
            return (Month: m, Hours: s);
        }).ToList();
        var max = Math.Max(0.1, byMonth.Max(x => x.Hours));
        Months = byMonth.Select(x => new StatsBar(x.Month.ToString("MMM", L.Culture), x.Hours / max,
            L.F("{0:MMMM yyyy}: {1:0.#} ч", x.Month, x.Hours))).ToList();
    }

    private static List<StatsRow> Top<TKey>(IEnumerable<IGrouping<TKey, (TrackItem Track, DateTime At)>> groups,
        Func<IGrouping<TKey, (TrackItem Track, DateTime At)>, string> name,
        Func<IGrouping<TKey, (TrackItem Track, DateTime At)>, string> sub,
        Func<IGrouping<TKey, (TrackItem Track, DateTime At)>, CollectionItem?> open)
    {
        var top = groups.OrderByDescending(g => g.Count()).Take(5).ToList();
        if (top.Count == 0) return [];
        var max = (double)top[0].Count();
        return top.Select((g, i) => new StatsRow(i + 1, name(g), sub(g),
            L.Plural(g.Count(), "раз", "раза", "раз", "time", "times"), g.Count() / max, open(g))).ToList();
    }
}
