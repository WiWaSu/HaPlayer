using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlayer.Ui;

namespace HaPlayer.ViewModels;

/// <summary>Что показывает дерево медиатеки в классическом виде.</summary>
public enum ClassicTreeMode
{
    Artists,
    Albums,
    Folders,
    Playlists,
}

/// <summary>
/// Узел дерева медиатеки: исполнитель с альбомами, альбом, папка с вложенными папками, плейлист.
/// Вложенные узлы строятся при первом обращении: папок бывает много, а раскрывают единицы.
/// </summary>
public sealed class ClassicNode(string name, string sub, Avalonia.Media.Geometry icon, CollectionItem item, Func<List<ClassicNode>>? children = null)
{
    private List<ClassicNode>? _children;

    public string Name { get; } = name;
    public string Sub { get; } = sub;
    public Avalonia.Media.Geometry Icon { get; } = icon;
    public CollectionItem Item { get; } = item;
    public List<ClassicNode> Children => _children ??= children?.Invoke() ?? [];
}

/// <summary>Вкладка над таблицей: открытый плейлист, альбом, исполнитель или папка.</summary>
public sealed partial class ClassicTab : ObservableObject
{
    public ClassicTab(CollectionItem item, Action<ClassicTab> select, Action<ClassicTab> close)
    {
        Item = item;
        SelectCommand = new RelayCommand(() => select(this));
        CloseCommand = new RelayCommand(() => close(this));
    }

    public CollectionItem Item { get; set; }
    public string Title => Item.Name;
    public string Key => Item.Kind + ":" + Item.Key;

    [ObservableProperty] private bool _isActive;

    public IRelayCommand SelectCommand { get; }
    public IRelayCommand CloseCommand { get; }

    public void Refresh() => OnPropertyChanged(nameof(Title));
}

/// <summary>
/// Таблица треков классического вида. Наследует PageViewModel, чтобы меню трека, Delete
/// и перетаскивание работали так же, как на страницах современного вида.
/// </summary>
public sealed partial class ClassicListViewModel : PageViewModel
{
    private readonly IReadOnlyList<TrackItem> _all;

    public ClassicListViewModel(MainViewModel main, CollectionItem? item, IReadOnlyList<TrackItem> tracks, string filter) : base(main)
    {
        Item = item;
        _all = tracks;
        _filter = filter;
        Apply();
    }

    public CollectionItem? Item { get; }
    public override PlaylistItem? Playlist => Item as PlaylistItem;
    public string Title => Item?.Name ?? "";

    [ObservableProperty] private List<TrackRow> _rows = [];

    private string _filter;

    public void SetFilter(string filter)
    {
        if (_filter == filter) return;
        _filter = filter;
        Apply();
    }

    private void Apply()
    {
        var q = _filter.Trim().ToLowerInvariant();
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IReadOnlyList<TrackItem> list = words.Length == 0 ? _all : _all.Where(t => words.All(t.SearchText.Contains)).ToList();
        Rows = Main.MakeRows(list, Title, (t, i) => (i + 1).ToString(), t => t.AlbumName, false);
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>«Muse · 12 треков · 40 мин», с фильтром — «5 из 12 треков».</summary>
    public string Status
    {
        get
        {
            var total = Format.Total(Rows.Sum(r => r.Item.Model.DurationSeconds));
            var count = Rows.Count == _all.Count
                ? Format.Tracks(Rows.Count)
                : L.F("{0} из {1}", Rows.Count, Format.Tracks(_all.Count));
            return Title.Length > 0 ? $"{Title} · {count} · {total}" : $"{count} · {total}";
        }
    }
}
