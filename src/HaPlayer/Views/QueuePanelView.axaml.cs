using Avalonia.Controls;
using HaPlayer.ViewModels;

namespace HaPlayer.Views;

public partial class QueuePanelView : UserControl
{
    public QueuePanelView()
    {
        InitializeComponent();
        // Перетащенные треки встают в конец очереди «Далее».
        TrackDrag.AttachTarget(this, _ => true, (_, tracks) => (DataContext as MainViewModel)?.Player.AddToQueue(tracks));
    }
}
