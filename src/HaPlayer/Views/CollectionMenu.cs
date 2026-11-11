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
