using HaPlayer.Ui;
using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using HaPlayer.ViewModels;

namespace HaPlayer.Views;

/// <summary>
/// Меню по правому клику на треке: играть, добавить в плейлист, перенести в альбом или к исполнителю.
/// Работает с несколькими выделенными треками сразу (Ctrl и Shift).
/// </summary>
public static class TrackMenu
{
    public static void Attach(ListBox list)
    {
        TrackDrag.AttachSource(list);
        list.AddHandler(InputElement.DoubleTappedEvent, (_, e) =>
        {
            if ((e.Source as Control)?.DataContext is TrackRow row) row.PlayCommand.Execute(null);
        });

        list.ContextRequested += (_, e) =>
        {
            if ((e.Source as Control)?.DataContext is not TrackRow row) return;
            if (list.DataContext is not PageViewModel page) return;

            // Щелчок по невыделенной строке выделяет только её, как в проводнике.
            if (list.SelectedItems?.Contains(row) != true) list.SelectedItem = row;
            var rows = list.SelectedItems?.OfType<TrackRow>().OrderBy(r => r.Index).ToList() ?? [row];
            var tracks = rows.Select(r => r.Item).ToList();

            var menu = new ContextMenu { ItemsSource = Build(page, row, tracks) };
            menu.Open(list);
            e.Handled = true;
        };
    }

    private static List<Control> Build(PageViewModel page, TrackRow clicked, List<TrackItem> tracks)
    {
        var main = page.Main;
        var single = tracks.Count == 1;
        var items = new List<Control>();

        if (single) items.Add(Item(L.T("Играть"), () => clicked.PlayCommand.Execute(null)));
        items.Add(Item(L.T("Играть следующим"), () => main.Player.PlayNext(tracks)));
        items.Add(Item(L.T("В очередь"), () => main.Player.AddToQueue(tracks)));
        items.Add(new Separator());

        var allLiked = tracks.All(t => t.IsLiked);
        items.Add(Item(allLiked ? L.T("Убрать из любимых") : L.T("Добавить в любимые"), () => main.ToggleLike(tracks)));

        // Плейлисты
        var toPlaylist = new MenuItem { Header = L.T("Добавить в плейлист") };
        toPlaylist.Items.Add(Item(L.T("Новый плейлист…"), () => main.CreatePlaylistAsync(tracks)));
        if (main.Playlists.Count > 0) toPlaylist.Items.Add(new Separator());
        foreach (var p in main.Playlists.Where(p => p != page.Playlist))
            toPlaylist.Items.Add(Item(p.Name, () => main.AddToPlaylist(p, tracks)));
        items.Add(toPlaylist);

        if (page.Playlist is { } current)
            items.Add(Item(single ? L.T("Убрать из плейлиста") : L.F("Убрать из плейлиста ({0})", tracks.Count),
                () => main.RemoveFromPlaylist(current, tracks)));

        // Альбом и исполнитель. Меняется только привязка в HaPlayer, теги файлов остаются прежними.
        // В строгом режиме этих пунктов нет: альбом и исполнитель берутся только из тегов.
        if (!main.Settings.StrictTags)
        {
            items.Add(new Separator());
            var toAlbum = new MenuItem { Header = L.T("Перенести в альбом") };
            toAlbum.Items.Add(Item(L.T("Новый альбом…"), () => main.AssignToNewAlbumAsync(tracks)));
            toAlbum.Items.Add(new Separator());
            foreach (var a in main.Albums)
                toAlbum.Items.Add(Item(a.Name, () => main.AssignToAlbum(a.Key, tracks)));
            items.Add(toAlbum);

            var toArtist = new MenuItem { Header = L.T("Указать исполнителя") };
            toArtist.Items.Add(Item(L.T("Новый исполнитель…"), () => main.AssignToNewArtistAsync(tracks)));
            toArtist.Items.Add(new Separator());
            foreach (var a in main.Artists)
                toArtist.Items.Add(Item(a.Name, () => main.AssignToArtist(a.Name, tracks)));
            items.Add(toArtist);

            if (tracks.Any(t => t.Model.UserAlbumKey != null || t.Model.UserArtist != null))
                items.Add(Item(L.T("Вернуть альбом и исполнителя из тегов"), () => main.ResetAssignments(tracks)));
        }

        if (single)
        {
            items.Add(new Separator());
            var t = tracks[0];
            if (t.Album is { } album) items.Add(Item(L.T("Перейти к альбому"), () => main.Open(album)));
            items.Add(Item(L.T("Перейти к исполнителю"), () => main.OpenArtist(t.Artist)));
            items.Add(Item(L.T("Показать в проводнике"), () => ShowInExplorer(t.Path)));
            items.Add(Item(L.T("Сведения о файле"), () => main.ShowFileInfoAsync(t)));
        }

        return items;
    }

    /// <summary>Открывает папку с файлом и выделяет его. Если файла уже нет, открывает папку.</summary>
    private static void ShowInExplorer(string path)
    {
        try
        {
            var args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch
        {
            // папки тоже нет: открывать нечего
        }
    }

    private static MenuItem Item(string header, Action action) =>
        new() { Header = header, Command = new RelayCommand(action) };

    private static MenuItem Item(string header, Func<Task> action) =>
        new() { Header = header, Command = new AsyncRelayCommand(action) };
}
