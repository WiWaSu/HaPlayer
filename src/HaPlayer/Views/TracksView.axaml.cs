using Avalonia.Controls;

namespace HaPlayer.Views;

public partial class TracksView : UserControl
{
    public TracksView()
    {
        InitializeComponent();
        TrackMenu.Attach(Rows);
    }
}
