using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.Input;
using HaPlayer.ViewModels;

namespace HaPlayer.Ui;

/// <summary>
/// Значок в трее, как у Spotify: щелчок открывает и прячет окно, правый клик — меню управления,
/// при наведении видно, что играет. Крестик окна прячет плеер в трей, если это включено в настройках.
/// </summary>
public sealed class TrayController
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly Window _window;
    private readonly MainViewModel _vm;
    private readonly TrayIcon _icon;
    private readonly NativeMenuItem _nowPlaying = new() { IsEnabled = false };
    private readonly NativeMenuItem _playPause = new();
    private bool _exiting;

    public TrayController(Application app, IClassicDesktopStyleApplicationLifetime desktop, Window window, MainViewModel vm)
    {
        _desktop = desktop;
        _window = window;
        _vm = vm;

        _playPause.Command = new RelayCommand(() => vm.Player.PlayPause());

        var volume = new NativeMenuItem(L.T("Громкость")) { Menu = new NativeMenu() };
        foreach (var level in new[] { 100, 75, 50, 25, 10 })
            volume.Menu.Add(new NativeMenuItem($"{level}%") { Command = new RelayCommand(() => vm.Player.Volume = level) });

        var menu = new NativeMenu
        {
            _nowPlaying,
            new NativeMenuItemSeparator(),
            _playPause,
            new NativeMenuItem(L.T("Следующий")) { Command = new RelayCommand(() => vm.Player.Next()) },
            new NativeMenuItem(L.T("Предыдущий")) { Command = new RelayCommand(() => vm.Player.Previous()) },
            volume,
            new NativeMenuItemSeparator(),
            new NativeMenuItem(L.T("Открыть HaPlayer")) { Command = new RelayCommand(ShowWindow) },
            new NativeMenuItem(L.T("Выход")) { Command = new RelayCommand(Exit) },
        };

        _icon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://HaPlayer/Assets/haplayer.ico"))),
            Menu = menu,
            IsVisible = true,
        };
        _icon.Clicked += (_, _) => ToggleWindow();
        TrayIcon.SetIcons(app, [_icon]);

        vm.Player.PropertyChanged += OnPlayerChanged;
        Update();

        // Крестик прячет окно в трей, если так настроено.
        window.Closing += (_, e) =>
        {
            if (_exiting || !vm.Settings.CloseToTray) return;
            e.Cancel = true;
            vm.SaveSession();
            window.Hide();
        };
    }

    /// <summary>Выход по-настоящему: из меню трея или при перезапуске.</summary>
    public void Exit()
    {
        // Выход закрывает окно, а закрытие окна снова зовёт Exit: второй вызов пропускаем.
        if (_exiting) return;
        _exiting = true;
        _icon.IsVisible = false;
        _desktop.Shutdown();
    }

    private void ShowWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ToggleWindow()
    {
        if (_window.IsVisible && _window.WindowState != WindowState.Minimized && _window.IsActive) _window.Hide();
        else ShowWindow();
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.CurrentTrack) or nameof(PlayerViewModel.IsPlaying)) Update();
    }

    private void Update()
    {
        var t = _vm.Player.CurrentTrack;
        var title = t == null ? L.T("Ничего не играет") : $"{t.Title} — {t.Artist}";
        _nowPlaying.Header = title;
        _playPause.Header = _vm.Player.IsPlaying ? L.T("Пауза") : L.T("Играть");
        // У подсказки значка в трее Windows есть предел длины.
        var tip = t == null ? "HaPlayer" : $"{title}\nHaPlayer";
        _icon.ToolTipText = tip.Length > 120 ? tip[..117] + "…" : tip;
    }
}
