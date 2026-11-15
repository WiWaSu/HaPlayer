using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using HaPlayer.ViewModels;

namespace HaPlayer.Views;

/// <summary>
/// Плашка при смене трека: обложка, название, исполнитель. Не забирает фокус, висит 4 секунды и тает.
/// Щелчок по ней открывает главное окно.
/// </summary>
public partial class TrackToastWindow : Window
{
    private readonly DispatcherTimer _hide;

    public TrackToastWindow()
    {
        InitializeComponent();
        _hide = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background, (_, _) => FadeOut());
        PointerPressed += (_, _) =>
        {
            Clicked?.Invoke();
            FadeOut();
        };
    }

    public Action? Clicked { get; set; }

    /// <summary>Угол экрана: "bottomright", "topright", "bottomleft", "topleft".</summary>
    public void ShowTrack(TrackItem track, string corner, Window anchor)
    {
        DataContext = track;
        var screen = anchor.Screens.ScreenFromWindow(anchor) ?? anchor.Screens.Primary;
        if (screen == null) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var w = (int)(Width * scale);
        var h = (int)(Height * scale);
        var margin = (int)(16 * scale);
        var x = corner.EndsWith("left") ? area.X + margin : area.Right - w - margin;
        var y = corner.StartsWith("top") ? area.Y + margin : area.Bottom - h - margin;
        Position = new PixelPoint(x, y);
        if (!IsVisible) Show();
        Card.Opacity = 1;
        _hide.Stop();
        _hide.Start();
    }

    private void FadeOut()
    {
        _hide.Stop();
        Card.Opacity = 0;
        DispatcherTimer.RunOnce(() =>
        {
            if (Card.Opacity == 0) Hide();
        }, TimeSpan.FromMilliseconds(300));
    }
}
