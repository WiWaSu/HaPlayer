using Avalonia.Controls;

namespace HaPlayer.Views;

public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
        TrackMenu.Attach(Rows);
    }
}
