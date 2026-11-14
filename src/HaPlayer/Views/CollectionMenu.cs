using HaPlayer.Ui;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using HaPlayer.ViewModels;

namespace HaPlayer.Views;

/// <summary>
/// Меню альбома, исполнителя или плейлиста. Одно и то же в «…» на странице и по правому клику в медиатеке.
/// </summary>
public static class CollectionMenu
{
    public static void Show(Control target, MainViewModel main, CollectionItem item, PlacementMode placement)
    {
        var menu = new ContextMenu { ItemsSource = Build(main, item), Placement = placement };
        menu.Open(target);
    }

    private static List<Control> Build(MainViewModel main, CollectionItem item)
    {
        var items = new List<Control>
        {
            Item(L.T("Играть"), () => main.Play(item)),
        };

        if (!item.IsBuiltInPinned && item is not SmartItem)
            items.Add(Item(item.IsUserPinned ? L.T("Открепить в медиатеке") : L.T("Закрепить в медиатеке"), () => main.TogglePin(item)));

        if (item.Tracks.Count > 0)
            items.Add(Item(L.T("Сохранить как .m3u8…"), () => main.ExportM3uAsync(item)));

        var canAssign = item is not (AlbumItem or ArtistItem) || !main.Settings.StrictTags;
        if (item is not (AllTracksItem or SmartItem) && canAssign)
        {
            items.Add(new Separator());
            items.Add(Item(L.T("Добавить песни с диска…"), () => main.AddSongsFromDiskAsync(item)));
            if (!item.IsLikedCollection) items.Add(Item(L.T("Сменить обложку…"), () => main.ChangePhotoAsync(item)));
            if (item is PlaylistItem p) items.Add(Item(L.T("Переименовать…"), () => main.RenamePlaylistAsync(p)));
        }

        // Объединение, склеивание, разделение: только привязки HaPlayer, в строгом режиме их нет.
        if (!main.Settings.StrictTags && item is ArtistItem artist)
        {
            var merge = new MenuItem { Header = L.T("Объединить с") };
            var key = MainViewModel.NameKey(artist.Name);
            foreach (var other in main.Artists.Where(a => a != artist)
                         .OrderBy(a => MainViewModel.NameKey(a.Name) == key ? 0 : MainViewModel.NameKey(a.Name).Contains(key) || key.Contains(MainViewModel.NameKey(a.Name)) ? 1 : 2)
                         .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).Take(40))
                merge.Items.Add(Item(other.Name, () => main.MergeArtist(artist, other)));
            if (merge.Items.Count > 0)
            {
                items.Add(new Separator());
                items.Add(merge);
            }
        }
        if (!main.Settings.StrictTags && item is AlbumItem album)
        {
            var glue = new MenuItem { Header = L.T("Склеить с") };
            foreach (var other in main.Albums.Where(a => a != album)
                         .OrderBy(a => string.Equals(a.Artist, album.Artist, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                         .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).Take(40))
                glue.Items.Add(Item($"{other.Name} — {other.Artist}", () => main.GlueAlbum(album, other)));
            items.Add(new Separator());
            if (glue.Items.Count > 0) items.Add(glue);
            if (album.Tracks.Select(t => Path.GetDirectoryName(t.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                items.Add(Item(L.T("Разделить по папкам"), () => main.SplitAlbumByFolders(album)));
            items.Add(Item(L.T("Заметка…"), () => main.EditNoteAsync("album", album.Key, album.Name)));
        }
        if (!main.Settings.StrictTags && item is AlbumItem or ArtistItem
            && item.Tracks.Any(t => t.Model.UserAlbumKey != null || t.Model.UserArtist != null))
            items.Add(Item(L.T("Вернуть как в тегах"), () => main.ResetAssignments(item.Tracks.ToList())));
        if (item is AlbumItem or ArtistItem)
            items.Add(Item(main.IsHiddenFromHome(item) ? L.T("Снова показывать на главной") : L.T("Не показывать на главной и в подборках"),
                () => main.ToggleHiddenFromHome(item)));

        var delete = item switch
        {
            PlaylistItem => L.T("Удалить плейлист"),
            AlbumItem => L.T("Убрать альбом из медиатеки"),
            ArtistItem => L.T("Убрать исполнителя из медиатеки"),
            _ => null,
        };
        if (delete != null)
        {
            items.Add(new Separator());
            var mi = Item(delete, () => main.DeleteCollectionAsync(item));
            mi.Classes.Add("danger");
            items.Add(mi);
        }
        return items;
    }

    private static MenuItem Item(string header, Action action) => new() { Header = header, Command = new RelayCommand(action) };

    private static MenuItem Item(string header, Func<Task> action) => new() { Header = header, Command = new AsyncRelayCommand(action) };
}
