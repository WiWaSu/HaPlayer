using Avalonia.Controls;

namespace HaPlayer.Views;

public partial class NowPlayingView : UserControl
{
    public NowPlayingView()
    {
        InitializeComponent();
        TrackDrag.AttachQueueReorder(UpNextList, () => (DataContext as HaPlayer.ViewModels.MainViewModel)?.Player);
    }
}
