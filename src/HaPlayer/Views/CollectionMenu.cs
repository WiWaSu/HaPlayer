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
        if (item is PlaylistFolderItem folder) return BuildFolder(main, folder);
        if (item is UserSmartItem smart) return BuildSmart(main, smart);

        var items = new List<Control>
        {
            Item(L.T("Играть"), () => main.Play(item)),
        };

        if (!item.IsBuiltInPinned && item is not SmartItem)
            items.Add(Item(item.IsUserPinned ? L.T("Открепить в медиатеке") : L.T("Закрепить в медиатеке"), () => main.TogglePin(item)));

        if (item.Tracks.Count > 0) items.Add(ExportMenu(main, item));

        var canAssign = item is not (AlbumItem or ArtistItem) || !main.Settings.StrictTags;
        if (item is not (AllTracksItem or SmartItem) && canAssign)
        {
            items.Add(new Separator());
            items.Add(Item(L.T("Добавить песни с диска…"), () => main.AddSongsFromDiskAsync(item)));
            if (!item.IsLikedCollection) items.Add(Item(L.T("Сменить обложку…"), () => main.ChangePhotoAsync(item)));
            if (item is PlaylistItem p)
            {
                items.Add(Item(L.T("Переименовать…"), () => main.RenamePlaylistAsync(p)));
                var current = main.FolderOf(p);
                var toFolder = new MenuItem { Header = L.T("В папку") };
                toFolder.Items.Add(Item(L.T("Новая папка…"), () => main.CreatePlaylistFolderAsync(p)));
                if (main.PlaylistFolderItems.Count > 0) toFolder.Items.Add(new Separator());
                foreach (var f in main.PlaylistFolderItems)
                {
                    var target = f;
                    toFolder.Items.Add(Item((f.Key == current?.Key ? "✓ " : "") + f.Name, () => main.MoveToFolder(p, target)));
                }
                if (current != null)
                {
                    toFolder.Items.Add(new Separator());
                    toFolder.Items.Add(Item(L.T("Убрать из папки"), () => main.MoveToFolder(p, null)));
                }
                items.Add(toFolder);

                var link = new MenuItem { Header = main.LinkedFile(p) != null ? "✓ " + L.T("Общий файл") : L.T("Общий файл") };
                if (main.LinkedFile(p) is { } linked)
                {
                    link.Items.Add(new MenuItem { Header = linked, IsEnabled = false });
                    link.Items.Add(new Separator());
                    link.Items.Add(Item(L.T("Отвязать от файла"), () => main.Unlink(p)));
                }
                else
                {
                    link.Items.Add(Item(L.T("Связать с новым файлом…"), () => main.LinkToNewFileAsync(p)));
                    link.Items.Add(Item(L.T("Связать с готовым файлом…"), () => main.LinkToExistingFileAsync(p)));
                }
                ToolTip.SetTip(link, L.T("Плейлист хранится ещё и в .m3u8, например в облачной или сетевой папке. Изменения идут в обе стороны."));
                items.Add(link);
            }
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
        if (item is ArtistItem ordered && main.HasAlbumOrder(ordered))
            items.Add(Item(L.T("Порядок альбомов по году"), () => main.ResetAlbumOrder(ordered)));
        if (item is AlbumItem or ArtistItem)
            items.Add(Item(main.IsHiddenFromHome(item) ? L.T("Снова показывать на главной") : L.T("Не показывать на главной и в подборках"),
                () => main.ToggleHiddenFromHome(item)));

        if (item is not AllTracksItem)
        {
            var banner = new MenuItem { Header = L.T("Шапка") };
            var current = main.Banner(item);
            MenuItem Mode(string header, string mode, Action act) =>
                new() { Header = (current == mode || (mode == "image" && current.StartsWith("image:")) ? "✓ " : "") + header, Command = new RelayCommand(act) };
            banner.Items.Add(Mode(L.T("Цвет обложки"), "color", () => main.SetBanner(item, "color")));
            banner.Items.Add(Mode(L.T("Анимированная абстракция"), "abstract", () => main.SetBanner(item, "abstract")));
            banner.Items.Add(new MenuItem { Header = (current.StartsWith("image:") ? "✓ " : "") + L.T("Своя картинка…"),
                Command = new AsyncRelayCommand(() => main.PickBannerAsync(item)) });
            items.Add(new Separator());
            items.Add(banner);
        }

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

    private static MenuItem ExportMenu(MainViewModel main, CollectionItem item)
    {
        var export = new MenuItem { Header = L.T("Сохранить как .m3u8") };
        export.Items.Add(Item(L.T("С полными путями…"), () => main.ExportM3uAsync(item)));
        var relative = Item(L.T("С относительными путями…"), () => main.ExportM3uAsync(item, relative: true));
        ToolTip.SetTip(relative, L.T("Пути от папки с плейлистом. Работает, если перенести плейлист вместе с музыкой на флешку или другой компьютер."));
        export.Items.Add(relative);
        return export;
    }

    private static List<Control> BuildSmart(MainViewModel main, UserSmartItem smart)
    {
        var items = new List<Control>();
        if (smart.Tracks.Count > 0)
        {
            items.Add(Item(L.T("Играть"), () => main.Play(smart)));
            items.Add(ExportMenu(main, smart));
        }
        items.Add(Item(smart.IsUserPinned ? L.T("Открепить в медиатеке") : L.T("Закрепить в медиатеке"), () => main.TogglePin(smart)));
        items.Add(Item(L.T("Изменить условия…"), () => main.EditSmartPlaylistAsync(smart)));
        items.Add(Item(L.T("Сменить обложку…"), () => main.ChangePhotoAsync(smart)));
        items.Add(new Separator());
        var mi = Item(L.T("Удалить умный плейлист"), () => main.DeleteSmartPlaylistAsync(smart));
        mi.Classes.Add("danger");
        items.Add(mi);
        return items;
    }

    private static List<Control> BuildFolder(MainViewModel main, PlaylistFolderItem folder)
    {
        var items = new List<Control>();
        if (folder.Tracks.Count > 0) items.Add(Item(L.T("Играть"), () => main.Play(folder)));
        items.Add(Item(folder.IsUserPinned ? L.T("Открепить в медиатеке") : L.T("Закрепить в медиатеке"), () => main.TogglePin(folder)));
        items.Add(Item(L.T("Переименовать…"), () => main.RenamePlaylistFolderAsync(folder)));
        items.Add(new Separator());
        var mi = Item(L.T("Удалить папку"), () => main.DeletePlaylistFolder(folder));
        mi.Classes.Add("danger");
        items.Add(mi);
        return items;
    }

    private static MenuItem Item(string header, Action action) => new() { Header = header, Command = new RelayCommand(action) };

    private static MenuItem Item(string header, Func<Task> action) => new() { Header = header, Command = new AsyncRelayCommand(action) };
}
