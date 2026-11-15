using Avalonia.Controls;
using Avalonia.Threading;
using HaPlayer.ViewModels;

namespace HaPlayer.Views;

public partial class SidebarView : UserControl
{
    public SidebarView()
    {
        InitializeComponent();

        // Открыли поиск по медиатеке — сразу ставим курсор в поле.
        LibrarySearch.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && LibrarySearch.IsVisible)
                Dispatcher.UIThread.Post(() => LibrarySearch.Focus(), DispatcherPriority.Input);
        };

        // Треки, перетащенные из списка: в плейлист добавляются, в «Любимые» отмечаются сердечком.
        TrackDrag.AttachTarget(LibraryList,
            ctx => ctx is PlaylistItem or LikedItem,
            (ctx, tracks) =>
            {
                if (DataContext is not MainViewModel main) return;
                if (ctx is PlaylistItem p) main.AddToPlaylist(p, tracks);
                else if (ctx is LikedItem) main.ToggleLike(tracks.Where(t => !t.IsLiked).ToList());
            });

        // Двойной щелчок включает плейлист, альбом или исполнителя. Одиночный открывает страницу.
        LibraryList.DoubleTapped += (_, e) =>
        {
            if ((e.Source as Control)?.DataContext is CollectionItem item && DataContext is MainViewModel main)
            {
                main.Play(item);
                e.Handled = true;
            }
        };

        // «Создать»: плейлист, умный плейлист или папка для плейлистов.
        CreateButton.Click += (_, _) =>
        {
            if (DataContext is not MainViewModel main) return;
            var menu = new ContextMenu
            {
                Placement = PlacementMode.BottomEdgeAlignedRight,
                ItemsSource = new List<Control>
                {
                    new MenuItem { Header = HaPlayer.Ui.L.T("Плейлист"), Command = main.CreatePlaylistCommand },
                    new MenuItem { Header = HaPlayer.Ui.L.T("Умный плейлист…"), Command = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => main.CreateSmartPlaylistAsync()) },
                    new MenuItem { Header = HaPlayer.Ui.L.T("Папка"), Command = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => main.CreatePlaylistFolderAsync()) },
                },
            };
            menu.Open(CreateButton);
        };

        // Правый клик по альбому, плейлисту или исполнителю: то же меню, что «…» на странице.
        LibraryList.ContextRequested += (_, e) =>
        {
            if ((e.Source as Control)?.DataContext is not CollectionItem item) return;
            if (DataContext is not MainViewModel main) return;
            CollectionMenu.Show(LibraryList, main, item, PlacementMode.Pointer);
            e.Handled = true;
        };
    }
}
