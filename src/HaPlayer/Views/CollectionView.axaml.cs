using Avalonia.Controls;
using HaPlayer.ViewModels;

namespace HaPlayer.Views;

public partial class CollectionView : UserControl
{
    public CollectionView()
    {
        InitializeComponent();
        ScrollMemory.Attach(this);
        TrackMenu.Attach(Rows);

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
}
