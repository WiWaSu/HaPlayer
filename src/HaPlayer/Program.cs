using Avalonia;

namespace HaPlayer;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Имя программы для Windows задаётся до первого окна: по нему панель мультимедиа узнаёт HaPlayer.
        Ui.AppIdentity.SetForProcess();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Inter вшит в программу: одинаковый вид на любой Windows, есть кириллица и все начертания.
            .WithInterFont()
            .LogToTrace();
}
