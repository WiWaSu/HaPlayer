using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HaPlayer.Library;
using HaPlayer.ViewModels;

namespace HaPlayer.Views;

public partial class MainWindow : Window, IDialogService
{
    public MainWindow()
    {
        InitializeComponent();

        // Перехватываем клавиши на пути вниз, раньше кнопок и ползунков:
        // иначе пробел «нажимал» бы кнопку в фокусе, а стрелки двигали бы ползунок.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
        // Окно потеряло фокус посреди нажатия: отпускание до нас не дойдёт, забываем нажатые клавиши.
        Deactivated += (_, _) => _pressed.Clear();
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Нажатие в поле поиска наверху открывает страницу поиска, как в Spotify.
        TopSearch.GotFocus += (_, _) =>
        {
            if (Vm is { CurrentPage: not SearchPageViewModel } vm) vm.GoSearch();
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm == null) return;
        Vm.Dialogs = this;
        Vm.FocusSearchRequested = () =>
        {
            TopSearch.Focus();
            TopSearch.CaretIndex = TopSearch.Text?.Length ?? 0;
        };
    }

    // ---------- Горячие клавиши: как в Winamp (Z X C V B) плюс пробел и стрелки ----------

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;

        if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.F or Key.K)
        {
            vm.IsNowPlayingOpen = false;
            vm.GoSearch();
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.Left or Key.Right)
        {
            if (e.Key == Key.Left) vm.GoBackCommand.Execute(null);
            else vm.GoForwardCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && (vm.IsNowPlayingOpen || vm.IsEqualizerOpen))
        {
            vm.IsNowPlayingOpen = false;
            vm.IsEqualizerOpen = false;
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && vm.IsSettingsActive)
        {
            vm.CloseSettings();
            e.Handled = true;
            return;
        }

        // В поле ввода буквы и пробел нужны для текста, а не для управления плеером.
        if (e.KeyModifiers != KeyModifiers.None || e.Source is TextBox) return;
        var p = vm.Player;
        Action? action = e.Key switch
        {
            Key.Space or Key.MediaPlayPause => p.PlayPause,
            Key.Z or Key.MediaPreviousTrack => p.Previous,
            Key.X => p.Play,
            Key.C => p.PlayPause,
            Key.V or Key.MediaStop => p.Stop,
            Key.B or Key.MediaNextTrack => p.Next,
            Key.S => p.ToggleShuffle,
            Key.R => p.CycleRepeat,
            Key.L => p.ToggleLike,
            Key.E => () => vm.IsEqualizerOpen = !vm.IsEqualizerOpen,
            Key.Left => () => p.SeekBy(-5),
            Key.Right => () => p.SeekBy(5),
            Key.Up => () => p.ChangeVolume(5),
            Key.Down => () => p.ChangeVolume(-5),
            _ => null,
        };
        if (action == null) return;
        e.Handled = true;

        // Удерживаемая клавиша присылает повторные нажатия. Для переключателей (пробел, S, R, L, E)
        // повтор превращал «пауза» в «пауза, играть». Срабатываем один раз за нажатие.
        // Стрелки повторяются: перемотка и громкость при удержании должны идти дальше.
        var repeats = e.Key is Key.Left or Key.Right or Key.Up or Key.Down;
        if (!repeats && !_pressed.Add(e.Key)) return;
        action();
    }

    private readonly HashSet<Key> _pressed = [];

    /// <summary>
    /// Отпускание клавиши тоже перехватываем: иначе кнопка в фокусе (например «играть», по которой
    /// перед этим щёлкнули мышью) получает отпущенный пробел и срабатывает второй раз.
    /// </summary>
    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (_pressed.Remove(e.Key) && e.Source is not TextBox) e.Handled = true;
    }

    // ---------- Перетаскивание файлов и папок ----------

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Files)) DropOverlay.IsVisible = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => DropOverlay.IsVisible = false;

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        var paths = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths is { Count: > 0 } && Vm != null) await Vm.ImportAsync(paths);
    }

    // ---------- Диалоги ----------

    public async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Добавить файлы",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Аудио") { Patterns = AudioFormats.Extensions.Select(x => "*" + x).ToArray() },
            ],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickImageAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите фото",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Изображения") { Patterns = AppPaths.ImageExtensions.Select(x => "*" + x).ToArray() },
            ],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().FirstOrDefault();
    }

    public async Task<IReadOnlyList<string>> PickPlaylistFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Плейлисты других плееров",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Плейлисты (M3U, PLS, XSPF, WPL, AIMP, iTunes XML)")
                {
                    Patterns = PlaylistImporter.Extensions.Select(x => "*" + x).ToArray(),
                },
            ],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickTorrentFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Торрент-файл",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Торрент-файлы") { Patterns = ["*.torrent"] }],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task CopyTextAsync(string text)
    {
        if (Clipboard != null) await Clipboard.SetTextAsync(text);
    }

    public Task<string?> PromptAsync(string title, string placeholder, string initial, string okText) =>
        Dialogs.PromptAsync(this, title, placeholder, initial, okText);

    public Task<bool> ConfirmAsync(string title, string message, string okText) =>
        Dialogs.ConfirmAsync(this, title, message, okText);

    public Task MessageAsync(string title, string message) => Dialogs.MessageAsync(this, title, message);

    public async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Папка с музыкой",
            AllowMultiple = false,
        });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }
}
