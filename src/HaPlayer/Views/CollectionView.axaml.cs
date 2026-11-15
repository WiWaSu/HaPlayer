using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HaPlayer.ViewModels;

namespace HaPlayer.Views;

public partial class CollectionView : UserControl
{
    public CollectionView()
    {
        InitializeComponent();
        ScrollMemory.Attach(this);
        TrackMenu.Attach(Rows);
        AttachAlbumReorder();

        // «Показать в медиатеке»: прокрутка к треку и выделение строки после раскладки страницы.
        CollectionPageViewModel? subscribed = null;
        void Reveal(TrackItem track) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is not CollectionPageViewModel vm) return;
            var row = vm.Rows.FirstOrDefault(r => r.Item == track);
            if (row == null) return;
            vm.PendingReveal = null;
            Rows.SelectedItem = row;
            Rows.ScrollIntoView(row);
        }, Avalonia.Threading.DispatcherPriority.Background);
        DataContextChanged += (_, _) =>
        {
            if (subscribed != null) subscribed.RevealRequested -= Reveal;
            subscribed = DataContext as CollectionPageViewModel;
            if (subscribed == null) return;
            subscribed.RevealRequested += Reveal;
            if (subscribed.PendingReveal is { } pending) Reveal(pending);
        };

        // «…»: закрепить, обложка, переименовать, удалить. То же меню открывается правым кликом в медиатеке.
        MoreButton.Click += (_, _) =>
        {
            if (DataContext is CollectionPageViewModel vm)
                CollectionMenu.Show(MoreButton, vm.Main, vm.Item, PlacementMode.BottomEdgeAlignedLeft);
        };
    }

    private const string AlbumFormat = "haplayer/album";

    /// <summary>
    /// Карточки альбомов на странице исполнителя можно перетаскивать. Пока тащат, остальные
    /// раздвигаются; порядок запоминается, когда карточку отпустили.
    /// </summary>
    private void AttachAlbumReorder()
    {
        Avalonia.Point? start = null;
        AlbumItem? dragged = null;
        var suppressClick = false;

        AlbumCards.AddHandler(PointerPressedEvent, (_, e) =>
        {
            start = null;
            if (!e.GetCurrentPoint(AlbumCards).Properties.IsLeftButtonPressed) return;
            if ((e.Source as Control)?.DataContext is not AlbumItem album) return;
            dragged = album;
            start = e.GetPosition(AlbumCards);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        AlbumCards.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            start = null;
            // Отпустили после перетаскивания: карточка не должна открыть альбом.
            if (suppressClick)
            {
                suppressClick = false;
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        AlbumCards.AddHandler(PointerMovedEvent, async (_, e) =>
        {
            if (start is not { } from || dragged == null) return;
            if (!e.GetCurrentPoint(AlbumCards).Properties.IsLeftButtonPressed)
            {
                start = null;
                return;
            }
            var d = e.GetPosition(AlbumCards) - from;
            if (Math.Abs(d.X) < 8 && Math.Abs(d.Y) < 8) return;
            start = null;
            if (DataContext is not CollectionPageViewModel vm || !vm.IsArtist) return;
            suppressClick = true;
            var data = new DataObject();
            data.Set(AlbumFormat, dragged);
            var result = await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
            vm.FinishAlbumDrag(result != DragDropEffects.None);
            dragged = null;
            // Отпускание кнопки мыши забирает сам перенос, и до карточки оно обычно не доходит.
            // Флаг снимаем, чтобы следующий обычный щелчок открыл альбом.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => suppressClick = false, Avalonia.Threading.DispatcherPriority.Background);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        DragDrop.SetAllowDrop(AlbumCards, true);
        AlbumCards.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (e.Data.Get(AlbumFormat) is not AlbumItem moving) return;
            e.DragEffects = DragDropEffects.Move;
            if ((e.Source as Control)?.DataContext is AlbumItem over && DataContext is CollectionPageViewModel vm)
                vm.MoveAlbumLive(moving, over);
            e.Handled = true;
        });
        AlbumCards.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (!e.Data.Contains(AlbumFormat)) return;
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        });
    }
}
