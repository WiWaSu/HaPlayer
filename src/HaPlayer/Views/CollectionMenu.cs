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
            Item("Играть", () => main.Play(item)),
        };

        if (!item.IsBuiltInPinned)
            items.Add(Item(item.IsUserPinned ? "Открепить в медиатеке" : "Закрепить в медиатеке", () => main.TogglePin(item)));

        if (item is not AllTracksItem)
        {
            items.Add(new Separator());
            items.Add(Item("Добавить песни с диска…", () => main.AddSongsFromDiskAsync(item)));
            if (!item.IsLikedCollection) items.Add(Item("Сменить обложку…", () => main.ChangePhotoAsync(item)));
            if (item is PlaylistItem p) items.Add(Item("Переименовать…", () => main.RenamePlaylistAsync(p)));
        }

        var delete = item switch
        {
            PlaylistItem => "Удалить плейлист",
            AlbumItem => "Убрать альбом из медиатеки",
            ArtistItem => "Убрать исполнителя из медиатеки",
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
