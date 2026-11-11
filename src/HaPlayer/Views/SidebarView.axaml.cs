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
