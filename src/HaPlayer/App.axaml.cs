using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HaPlayer.Audio;
using HaPlayer.Library;
using HaPlayer.ViewModels;
using HaPlayer.Views;

namespace HaPlayer;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // После обновления убираем прежний exe (HaPlayer.exe.old) и недокачанное.
            UpdateService.CleanupAfterUpdate();

            var dataDir = AppPaths.DataDirectory;
            var db = new LibraryDb(Path.Combine(dataDir, "library.db"));
            var settings = Settings.Load(Path.Combine(dataDir, "settings.json"));
            var engine = new NAudioEngine { Output = settings.Output, LatencyMs = settings.LatencyMs };
            var covers = new CoverService();

            // Тема и форматы применяются до создания окна, чтобы оно сразу открылось в нужных цветах.
            Ui.ThemeService.Apply(settings.Palette, settings.Surface, settings.CustomAccent);
            AudioFormats.SetDisabled(settings.DisabledFormats);

            var vm = new MainViewModel(db, settings, engine, covers);
            vm.RestoreSession();

            var window = new MainWindow { DataContext = vm };
            if (settings.WindowWidth >= 960 && settings.WindowHeight >= 620)
            {
                window.Width = settings.WindowWidth;
                window.Height = settings.WindowHeight;
            }
            if (settings.WindowMaximized) window.WindowState = WindowState.Maximized;
            desktop.MainWindow = window;

            // При закрытии запоминаем страницу, очередь, позицию трека и размер окна.
            window.Closing += (_, _) =>
            {
                settings.WindowMaximized = window.WindowState == WindowState.Maximized;
                if (window.WindowState == WindowState.Normal)
                {
                    settings.WindowWidth = window.Width;
                    settings.WindowHeight = window.Height;
                }
                vm.SaveSession();
            };

            // Значок в трее. Крестик прячет окно туда, если включено «Сворачивать в трей».
            var tray = new Ui.TrayController(this, desktop, window, vm);
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            vm.ExitRequested = tray.Exit;
            window.Closed += (_, _) => tray.Exit();

            vm.RestartRequested = () =>
            {
                vm.SaveSession();
                if (Environment.ProcessPath is { } exe) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                tray.Exit();
            };

            // Проверка папок при запуске: только новые и изменённые файлы, в фоне, после показа окна.
            if (settings.RescanOnStart && settings.MusicFolders.Count > 0)
                window.Opened += async (_, _) => await vm.RescanAsync();

            window.Opened += async (_, _) => await vm.CheckUpdatesOnStartAsync();

            // Торренты: если были включены, продолжаем раздачи после показа окна.
            if (settings.TorrentsEnabled)
                window.Opened += async (_, _) => await vm.Torrents.StartIfEnabledAsync();

            desktop.Exit += (_, _) =>
            {
                vm.Torrents.Shutdown();
                settings.Save();
                engine.Dispose();
                db.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
