using HaPlayer.Ui;
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
        // Вёрстка подстраивается под ширину окна: от половины ноутбучного экрана до широкого монитора.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == ClientSizeProperty) ApplyLayout(ClientSize.Width / _scale);
        };

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
        Vm.Notify = ShowNotification;
        Vm.ScaleChanged = ApplyScale;
        Vm.DensityChanged = ApplyDensity;
        ApplyDensity(Vm.Settings.Dense);
        ApplyScale(Vm.Settings.UiScale);
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

        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.K)
        {
            if (vm.Palette.IsOpen) vm.Palette.Close();
            else
            {
                vm.Palette.Open();
                Avalonia.Threading.Dispatcher.UIThread.Post(() => PaletteBox.Focus(), Avalonia.Threading.DispatcherPriority.Input);
            }
            e.Handled = true;
            return;
        }
        if (vm.Palette.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Escape: vm.Palette.Close(); e.Handled = true; return;
                case Key.Down: vm.Palette.Move(1); e.Handled = true; return;
                case Key.Up: vm.Palette.Move(-1); e.Handled = true; return;
                case Key.Enter: vm.Palette.RunSelected(); e.Handled = true; return;
            }
            return;
        }
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.F)
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

        // В поле ввода буквы и пробел печатают текст и плеером не управляют.
        if (e.KeyModifiers != KeyModifiers.None || e.Source is TextBox) return;
        // Медиаклавиши при включённых глобальных клавишах ловит система: здесь пропускаем, иначе сработало бы дважды.
        if (vm.GlobalHotkeysActive && e.Key is Key.MediaPlayPause or Key.MediaNextTrack or Key.MediaPreviousTrack or Key.MediaStop) return;
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

    // ---------- Палитра команд ----------

    private void OnPaletteBackdrop(object? sender, Avalonia.Input.PointerPressedEventArgs e) => Vm?.Palette.Close();

    private void OnPaletteItemTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PaletteItem item && Vm is { } vm)
        {
            vm.Palette.SelectedIndex = vm.Palette.Results.IndexOf(item);
            vm.Palette.RunSelected();
        }
    }

    // ---------- Уведомления ----------

    private Avalonia.Controls.Notifications.WindowNotificationManager? _notifications;

    /// <summary>Карточка в правом нижнем углу над панелью плеера. Висит, пока её не нажмут или не закроют.</summary>
    public void ShowNotification(string title, string message, Action onClick)
    {
        _notifications ??= new Avalonia.Controls.Notifications.WindowNotificationManager(this)
        {
            Position = Avalonia.Controls.Notifications.NotificationPosition.BottomRight,
            MaxItems = 2,
            Margin = new Avalonia.Thickness(0, 0, 16, 96),
        };
        _notifications.Show(new Avalonia.Controls.Notifications.Notification(title, message,
            Avalonia.Controls.Notifications.NotificationType.Information, TimeSpan.Zero, onClick));
    }

    // ---------- Масштаб ----------

    private double _scale = 1;

    /// <summary>Увеличивает или уменьшает весь интерфейс окна. Ступени ширины считаются уже в новом масштабе.</summary>
    public void ApplyScale(double scale)
    {
        _scale = Math.Clamp(scale, 0.75, 1.5);
        Scaler.LayoutTransform = Math.Abs(_scale - 1) < 0.01 ? null : new Avalonia.Media.ScaleTransform(_scale, _scale);
        ExtendClientAreaTitleBarHeightHint = Root.RowDefinitions[0].Height.Value * _scale;
        ApplyLayout(ClientSize.Width / _scale);
    }

    // ---------- Плотность ----------

    /// <summary>Плотный вид: ниже верхняя и нижняя панели, мельче обложки и шапки (стили класса dense).</summary>
    public void ApplyDensity(bool dense)
    {
        Classes.Set("dense", dense);
        Root.RowDefinitions[0].Height = new GridLength(dense ? 52 : 64);
        Root.RowDefinitions[2].Height = new GridLength(dense ? 68 : 80);
        ExtendClientAreaTitleBarHeightHint = Root.RowDefinitions[0].Height.Value * _scale;
    }

    // ---------- Ширина окна ----------

    /// <summary>
    /// Три ступени. Широкое окно: всё как есть. Узкое (меньше 1180): медиатека слева сжимается до обложек,
    /// в списках треков пропадает дата. Совсем узкое (меньше 900): прячется правая панель и колонка альбома.
    /// </summary>
    private void ApplyLayout(double width)
    {
        var narrow = width < 1180;
        var tiny = width < 900;
        Classes.Set("medium", width < 1440);
        Classes.Set("narrow", narrow);
        Classes.Set("tiny", tiny);
        Sidebar.Classes.Set("compact", narrow);
        Root.ColumnDefinitions[0].Width = new GridLength(narrow ? 88 : width >= 1800 ? 420 : 370);
        RightPanels.IsVisible = !tiny;
        TopSearchBox.Width = tiny ? 200 : narrow ? 340 : 470;
    }

    // ---------- Перетаскивание файлов и папок ----------

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Files) && !e.Data.Contains(TrackDrag.Format)) DropOverlay.IsVisible = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => DropOverlay.IsVisible = false;

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.Data.Contains(DataFormats.Files) && !e.Data.Contains(TrackDrag.Format)
            ? DragDropEffects.Copy : DragDropEffects.None;

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
            Title = L.T("Добавить файлы"),
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType(L.T("Аудио")) { Patterns = AudioFormats.Extensions.Select(x => "*" + x).ToArray() },
            ],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickImageAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("Выберите фото"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(L.T("Изображения")) { Patterns = AppPaths.ImageExtensions.Select(x => "*" + x).ToArray() },
            ],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().FirstOrDefault();
    }

    public async Task<IReadOnlyList<string>> PickPlaylistFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("Плейлисты других плееров"),
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType(L.T("Плейлисты (M3U, PLS, XSPF, WPL, AIMP, iTunes XML)"))
                {
                    Patterns = PlaylistImporter.Extensions.Select(x => "*" + x).ToArray(),
                },
            ],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName, string extension)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(suggestedName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("Сохранить плейлист"),
            SuggestedFileName = name + extension,
            DefaultExtension = extension.TrimStart('.'),
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(L.T("Плейлист M3U")) { Patterns = ["*" + extension, "*.m3u"] }],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickTorrentFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("Торрент-файл"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(L.T("Торрент-файлы")) { Patterns = ["*.torrent"] }],
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
            Title = L.T("Папка с музыкой"),
            AllowMultiple = false,
        });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }
}
