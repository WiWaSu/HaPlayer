using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlayer.Ui;

namespace HaPlayer.ViewModels;

/// <summary>Строка палитры команд: что это, подпись, значок и что сделать по Enter.</summary>
public sealed record PaletteItem(string Title, string Sub, Geometry Icon, Action Run);

/// <summary>
/// Палитра команд по Ctrl+K: быстрый переход к треку, альбому, исполнителю, плейлисту или действию.
/// Все слова запроса должны найтись, как в поиске.
/// </summary>
public sealed partial class PaletteViewModel(MainViewModel main) : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private List<PaletteItem> _results = [];
    [ObservableProperty] private int _selectedIndex;

    public void Open()
    {
        Query = "";
        Refresh();
        IsOpen = true;
    }

    [RelayCommand]
    public void Close() => IsOpen = false;

    partial void OnQueryChanged(string value) => Refresh();

    public void Move(int delta)
    {
        if (Results.Count == 0) return;
        SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, Results.Count - 1);
    }

    public void RunSelected()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Results.Count) return;
        var item = Results[SelectedIndex];
        IsOpen = false;
        // Действие после закрытия: иначе фокус вернулся бы в палитру.
        Dispatcher.UIThread.Post(item.Run);
    }

    [RelayCommand]
    private void Run(PaletteItem item)
    {
        IsOpen = false;
        Dispatcher.UIThread.Post(item.Run);
    }

    private IEnumerable<PaletteItem> Commands()
    {
        var p = main.Player;
        yield return new(L.T("Главная"), L.T("Перейти"), Icons.Home, () => main.GoHomeCommand.Execute(null));
        yield return new(L.T("Все треки"), L.T("Перейти"), Icons.Library, () => main.ShowTracks(""));
        yield return new(L.T("Итоги"), L.T("Перейти"), Icons.Stats, () => main.GoStatsCommand.Execute(null));
        yield return new(L.T("По годам"), L.T("Перейти"), Icons.Clock, () => main.GoYearsCommand.Execute(null));
        yield return new(L.T("Настройки"), L.T("Перейти"), Icons.Settings, () => main.GoSettingsCommand.Execute(null));
        yield return new(L.T("История прослушивания"), L.T("Открыть"), Icons.History, () => main.ToggleHistoryPanelCommand.Execute(null));
        yield return new(L.T("Сейчас играет"), L.T("Открыть"), Icons.Maximize, () => main.ToggleNowPlayingCommand.Execute(null));
        yield return new(L.T("Мини-плеер"), L.T("Открыть"), Icons.MiniPlayer, () => main.ToggleMiniPlayerCommand.Execute(null));
        yield return new(L.T("Эквалайзер"), L.T("Открыть"), Icons.Equalizer, () => main.ToggleEqualizerCommand.Execute(null));
        yield return new(L.T("Перемешать всю медиатеку"), L.T("Играть"), Icons.Shuffle, () =>
        {
            if (main.Library.Count == 0) return;
            p.Shuffle = true;
            p.PlayList(main.Library, Random.Shared.Next(main.Library.Count), L.T("Все треки"));
        });
        foreach (var m in new[] { 15, 30, 60 })
            yield return new(L.F("Таймер сна: {0} мин", m), L.T("Остановить музыку"), Icons.Moon, () => main.SetSleepTimer(m));
        yield return new(L.T("Таймер сна: после этого трека"), L.T("Остановить музыку"), Icons.Moon, () => main.SetSleepTimer(-1));
        if (main.IsSleepActive)
            yield return new(L.T("Выключить таймер сна"), "", Icons.Moon, () => main.SetSleepTimer(0));
        if (p.CurrentTrack is { } t)
        {
            yield return new(L.T("Показать в медиатеке"), t.Title, Icons.Library, main.RevealCurrent);
            yield return new(L.T("Радио по треку"), t.Title, Icons.Sparkles, () => main.PlayRadio(t));
            yield return new(L.T("Сведения о файле"), t.Title, Icons.Note, () => _ = main.ShowFileInfoAsync(t));
        }
    }

    private void Refresh()
    {
        var words = (Query ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Match(string text) => words.All(text.ToLowerInvariant().Contains);

        var list = new List<PaletteItem>();
        list.AddRange(Commands().Where(c => words.Length == 0 || Match(c.Title)).Take(words.Length == 0 ? 8 : 6));
        if (words.Length > 0)
        {
            foreach (var a in main.AllPlaylists.Concat(main.SmartPlaylists).Where(c => Match(c.Name)).Take(4))
                list.Add(new(a.Name, a.Subtitle, Icons.PlaylistAdd, () => main.Open(a)));
            foreach (var a in main.Artists.Where(a => Match(a.Name)).Take(4))
                list.Add(new(a.Name, L.T("Исполнитель"), Icons.Person, () => main.Open(a)));
            foreach (var a in main.Albums.Where(a => Match($"{a.Name} {a.Artist}")).Take(5))
                list.Add(new(a.Name, L.F("Альбом • {0}", a.Artist), Icons.Library, () => main.Open(a)));
            foreach (var t in main.Library.Where(t => Match(t.SearchText)).Take(8))
            {
                var track = t;
                list.Add(new(t.Title, t.Artist, Icons.Note, () =>
                {
                    var album = track.Album;
                    if (album != null) main.Player.PlayList(album.Tracks, Math.Max(0, album.Tracks.IndexOf(track)), album.Name);
                    else main.Player.PlayList([track], 0, track.Title);
                }));
            }
        }
        Results = list;
        SelectedIndex = list.Count > 0 ? 0 : -1;
    }
}
