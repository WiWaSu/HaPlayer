using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using HaPlayer.ViewModels;

namespace HaPlayer.Views;

public partial class ClassicView : UserControl
{
    private GridLength _treeWidth = new(300);

    public ClassicView()
    {
        InitializeComponent();
        // Меню трека, Delete, двойной щелчок и перетаскивание: то же, что в списках современного вида.
        TrackMenu.Attach(Rows);

        // Щелчок по узлу дерева открывает его во вкладке, двойной сразу включает.
        Tree.SelectionChanged += (_, _) =>
        {
            if (Tree.SelectedItem is ClassicNode node && DataContext is MainViewModel vm) vm.OpenInClassic(node.Item);
        };
        Tree.DoubleTapped += (_, e) =>
        {
            if ((e.Source as Control)?.DataContext is ClassicNode node && DataContext is MainViewModel vm)
            {
                vm.PlayFromTree(node.Item);
                e.Handled = true;
            }
        };
        Tree.ContextRequested += (_, e) =>
        {
            if ((e.Source as Control)?.DataContext is not ClassicNode node || DataContext is not MainViewModel vm) return;
            CollectionMenu.Show(Tree, vm, node.Item, PlacementMode.Pointer);
            e.Handled = true;
        };

        // Треки, перетащенные на плейлист в дереве, добавляются в него.
        TrackDrag.AttachTarget(Tree,
            ctx => ctx is ClassicNode { Item: PlaylistItem or LikedItem },
            (ctx, tracks) =>
            {
                if (DataContext is not MainViewModel vm || ctx is not ClassicNode node) return;
                if (node.Item is PlaylistItem p) vm.AddToPlaylist(p, tracks);
                else vm.ToggleLike(tracks.Where(t => !t.IsLiked).ToList());
            });

        SleepButton.Click += (_, _) =>
        {
            if (DataContext is MainViewModel vm) SleepMenu.Show(SleepButton, vm, PlacementMode.BottomEdgeAlignedRight);
        };

        // Совсем узкое окно: дерево прячется, таблице нужнее место. Ширину, которую задали разделителем, помним.
        SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
    }

    private void ApplyWidth(double width)
    {
        var column = Body.ColumnDefinitions[0];
        var hide = width < 820;
        if (hide && TreePanel.IsVisible)
        {
            _treeWidth = column.Width;
            column.Width = new GridLength(0);
            Body.ColumnDefinitions[1].Width = new GridLength(0);
        }
        else if (!hide && !TreePanel.IsVisible)
        {
            column.Width = _treeWidth;
            Body.ColumnDefinitions[1].Width = new GridLength(6);
        }
        TreePanel.IsVisible = !hide;
        Splitter.IsVisible = !hide;
        var narrow = width < 1000;
        TimeBlock.IsVisible = !narrow;
        SmallTime.IsVisible = narrow;
    }
}
