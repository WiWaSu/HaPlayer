using Avalonia.Controls;
using HaPlayer.ViewModels;

namespace HaPlayer.Views;

public partial class CollectionView : UserControl
{
    public CollectionView()
    {
        InitializeComponent();
        TrackMenu.Attach(Rows);

        // «…»: закрепить, обложка, переименовать, удалить. То же меню открывается правым кликом в медиатеке.
        MoreButton.Click += (_, _) =>
        {
            if (DataContext is CollectionPageViewModel vm)
                CollectionMenu.Show(MoreButton, vm.Main, vm.Item, PlacementMode.BottomEdgeAlignedLeft);
        };
    }
}
