using HaPlayer.Ui;

namespace HaPlayer.ViewModels;

/// <summary>Год и его альбомы.</summary>
public sealed record YearGroup(string Year, string Count, List<AlbumItem> Albums);

/// <summary>Альбомы медиатеки по годам выхода, от новых к старым. Альбомы без года в конце.</summary>
public sealed class YearsPageViewModel : PageViewModel
{
    public YearsPageViewModel(MainViewModel main) : base(main)
    {
        Groups = main.Albums
            .GroupBy(a => a.Year)
            .OrderByDescending(g => g.Key > 0 ? g.Key : int.MinValue)
            .Select(g => new YearGroup(
                g.Key > 0 ? g.Key.ToString() : L.T("Без года"),
                L.Plural(g.Count(), "альбом", "альбома", "альбомов", "album", "albums"),
                g.OrderBy(a => a.Artist, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Name).ToList()))
            .ToList();
    }

    public List<YearGroup> Groups { get; }
}
