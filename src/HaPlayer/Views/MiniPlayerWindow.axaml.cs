using Avalonia.Controls;
using Avalonia.Input;

namespace HaPlayer.Views;

public partial class MiniPlayerWindow : Window
{
    public MiniPlayerWindow()
    {
        InitializeComponent();
        // Окно без рамки: тащим за любое место, кроме кнопок.
        PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button) BeginMoveDrag(e);
        };
        // Закрыть мини-плеер с клавиатуры нельзя случайно: Alt+F4 только прячет его, как и кнопка.
        Closing += (_, e) =>
        {
            if (!ReallyClose && e.CloseReason != WindowCloseReason.ApplicationShutdown)
            {
                e.Cancel = true;
                CloseRequested?.Invoke();
            }
        };
    }

    public bool ReallyClose { get; set; }
    public Action? CloseRequested { get; set; }
}
