using Avalonia.Controls;

namespace HaPlayer.Views;

public partial class YearsView : UserControl
{
    public YearsView()
    {
        InitializeComponent();
        ScrollMemory.Attach(this);
    }
}
