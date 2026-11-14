using Avalonia.Controls;

namespace HaPlayer.Views;

public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
        ScrollMemory.Attach(this);
        TrackMenu.Attach(Rows);
    }
}
