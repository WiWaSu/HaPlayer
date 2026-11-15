using Avalonia.Controls;

namespace HaPlayer.Views;

public partial class TracksView : UserControl
{
    public TracksView()
    {
        InitializeComponent();
        ScrollMemory.Attach(this);
        TrackMenu.Attach(Rows);
    }
}
