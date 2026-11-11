using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlayer.Audio;
using HaPlayer.Library;
using HaPlayer.Ui;

namespace HaPlayer.ViewModels;

public sealed class FolderRow(string path, Func<string, Task> remove)
{
    public string Path { get; } = path;
    public bool Exists { get; } = Directory.Exists(path);
    public string Status => Exists ? "" : "папка не найдена";
    public IAsyncRelayCommand RemoveCommand { get; } = new AsyncRelayCommand(() => remove(path));
}

public sealed partial class FormatOption(string name, string description, string[] extensions, bool enabled, Action changed)
    : ObservableObject
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public string[] Extensions { get; } = extensions;

    [ObservableProperty] private bool _isEnabled = enabled;

    partial void OnIsEnabledChanged(bool value) => changed();
}

public sealed partial class ChoiceOption(string id, string name, IBrush swatch, IBrush swatch2, Action<string> select)
    : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public IBrush Swatch { get; } = swatch;
    public IBrush Swatch2 { get; } = swatch2;
    public IRelayCommand SelectCommand { get; } = new RelayCommand(() => select(id));

    [ObservableProperty] private bool _isSelected;
}

public sealed record HotkeyRow(string Keys, string Action);

public sealed class DataModeOption(DataMode mode, string title, string description, bool isCurrent, Func<DataMode, Task> select)
{
    public DataMode Mode { get; } = mode;
    public string Title { get; } = title;
    public string Description { get; } = description;
    public bool IsCurrent { get; } = isCurrent;
    public string ButtonText => IsCurrent ? "Сейчас" : Mode == DataMode.Custom ? "Выбрать папку" : "Перейти";
    public IAsyncRelayCommand SelectCommand { get; } = new AsyncRelayCommand(() => select(mode));
}

public sealed partial class SettingsPageViewModel : PageViewModel
{
    private readonly Settings _s;

    public SettingsPageViewModel(MainViewModel main) : base(main)
    {
        _s = main.Settings;

        foreach (var f in _s.MusicFolders) Folders.Add(new FolderRow(f, RemoveFolderAsync));

        var disabled = new HashSet<string>(_s.DisabledFormats, StringComparer.OrdinalIgnoreCase);
        Formats = AudioFormats.Groups
            .Select(g => new FormatOption(g.Name, g.Description, g.Extensions, !g.Extensions.All(disabled.Contains), OnFormatsChanged))
            .ToList();

        Palettes = ThemeService.Palettes
            .Select(p => new ChoiceOption(p.Id, p.Name, new SolidColorBrush(p.Accent), new SolidColorBrush(p.Highlight), SelectPalette))
            .ToList();
        Surfaces = ThemeService.Surfaces
            .Select(s => new ChoiceOption(s.Id, s.Name, new SolidColorBrush(s.Panel), new SolidColorBrush(s.Background), SelectSurface))
            .ToList();
        MarkSelected();

        _rescanOnStart = _s.RescanOnStart;
        _keepHistory = _s.KeepHistory;
        _customAccentText = _s.CustomAccent ?? "";
        _headerFromCover = _s.HeaderFromCover;
        _selectedOutput = Outputs.FirstOrDefault(o => o.Id == _s.Output) ?? Outputs[0];
        _latency = _s.LatencyMs;

        AddFolderCommand2 = new AsyncRelayCommand(async () =>
        {
            await main.AddMusicFolderAsync();
            ReloadFolders();
        });
        RescanCommand = new AsyncRelayCommand(main.RescanAsync);
        ImportPlaylistsCommand = main.ImportPlaylistsCommand;

        AddArtistFolderCommand = new AsyncRelayCommand(async () =>
        {
            await main.AddArtistFolderAsync();
            ReloadFolders();
        });
        DownloadByLinkCommand = new AsyncRelayCommand(async () =>
        {
            await main.DownloadFromLinksAsync();
            ReloadFolders();
        });
        ChangeDownloadFolderCommand = new AsyncRelayCommand(async () =>
        {
            if (main.Dialogs == null) return;
            var folder = await main.Dialogs.PickFolderAsync();
            if (folder == null) return;
            main.SetDownloadFolder(folder);
            OnDownloadFolderChanged();
        });
        ResetDownloadFolderCommand = new RelayCommand(() =>
        {
            main.SetDownloadFolder(null);
            OnDownloadFolderChanged();
        });
    }

    private void OnDownloadFolderChanged()
    {
        OnPropertyChanged(nameof(DownloadFolder));
        OnPropertyChanged(nameof(DownloadFolderHint));
        OnPropertyChanged(nameof(IsCustomDownloadFolder));
    }

    public bool IsCustomDownloadFolder => Main.IsCustomDownloadFolder;

    public string DownloadFolderHint => Main.IsCustomDownloadFolder
        ? "Выбрана вручную"
        : Main.Settings.MusicFolders.Any(Directory.Exists)
            ? "Ваша папка с музыкой"
            : "Папок с музыкой пока нет, поэтому сюда";

    public IRelayCommand ResetDownloadFolderCommand { get; }

    // ---------- Скачивание по ссылке ----------

    public IAsyncRelayCommand AddArtistFolderCommand { get; private set; } = null!;

    public string DownloadFolder => Main.DownloadFolder;
    public IAsyncRelayCommand DownloadByLinkCommand { get; }
    public IAsyncRelayCommand ChangeDownloadFolderCommand { get; }

    // ---------- Категории ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLibraryCat), nameof(IsSoundCat), nameof(IsLookCat), nameof(IsDownloadsCat),
        nameof(IsDataCat), nameof(IsKeysCat), nameof(IsAppCat))]
    private string _category = "library";

    public bool IsLibraryCat => Category == "library";
    public bool IsSoundCat => Category == "sound";
    public bool IsLookCat => Category == "look";
    public bool IsDownloadsCat => Category == "downloads";
    public bool IsDataCat => Category == "data";
    public bool IsKeysCat => Category == "keys";
    public bool IsAppCat => Category == "app";

    [RelayCommand]
    private void SelectCategory(string category) => Category = category;

    /// <summary>Серая строка внизу настроек.</summary>
    public string FooterText => $"HaPlayer {Version} · без телеметрии · лицензия не выбрана";

    // ---------- Свой акцентный цвет ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomPreview), nameof(CustomValid))]
    private string _customAccentText = "";

    public bool CustomValid => HaPlayer.Ui.ThemeService.ParseHex(CustomAccentText) != null;

    public IBrush CustomPreview => HaPlayer.Ui.ThemeService.ParseHex(CustomAccentText) is { } c
        ? new SolidColorBrush(c)
        : new SolidColorBrush(HaPlayer.Ui.ThemeService.Current.Accent);

    public bool HasCustomAccent => !string.IsNullOrEmpty(_s.CustomAccent);

    [RelayCommand]
    private void ApplyCustomAccent()
    {
        if (HaPlayer.Ui.ThemeService.ParseHex(CustomAccentText) is not { } c) return;
        _s.CustomAccent = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        _s.Save();
        OnPropertyChanged(nameof(HasCustomAccent));
        Main.ApplyTheme();
    }

    [RelayCommand]
    private void ResetCustomAccent()
    {
        _s.CustomAccent = null;
        _s.Save();
        CustomAccentText = "";
        OnPropertyChanged(nameof(HasCustomAccent));
        Main.ApplyTheme();
    }

    // ---------- Окно и обновления ----------

    public bool CloseToTray
    {
        get => _s.CloseToTray;
        set
        {
            _s.CloseToTray = value;
            _s.Save();
            OnPropertyChanged();
        }
    }

    public bool HasRepo => UpdateService.HasRepo;

    public bool CheckUpdates
    {
        get => _s.CheckUpdates;
        set
        {
            _s.CheckUpdates = value;
            _s.Save();
            OnPropertyChanged();
        }
    }

    [ObservableProperty] private string _updateStatus = "";

    [RelayCommand]
    private async Task CheckUpdatesNow()
    {
        UpdateStatus = "Проверяю…";
        var release = await UpdateService.CheckAsync();
        UpdateStatus = release == null ? $"У вас последняя версия ({Version})" : $"Есть версия {release.Version}";
        Main.AvailableUpdate = release;
    }

    [RelayCommand]
    private void OpenGitHub() => Open(UpdateService.RepoUrl);

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // браузер не открылся
        }
    }

    // ---------- Медиатека ----------

    public ObservableCollection<FolderRow> Folders { get; } = [];
    public bool HasNoFolders => Folders.Count == 0;
    public string LibrarySummary => $"В медиатеке {Format.Tracks(Main.Library.Count)}, альбомов: {Main.Albums.Count}, исполнителей: {Main.Artists.Count}.";

    public IAsyncRelayCommand AddFolderCommand2 { get; }
    public IAsyncRelayCommand RescanCommand { get; }
    public IAsyncRelayCommand ImportPlaylistsCommand { get; }

    [ObservableProperty] private bool _rescanOnStart;

    [ObservableProperty] private bool _keepHistory;

    partial void OnKeepHistoryChanged(bool value)
    {
        _s.KeepHistory = value;
        _s.Save();
        Main.OnKeepHistoryChanged();
    }

    partial void OnRescanOnStartChanged(bool value)
    {
        _s.RescanOnStart = value;
        _s.Save();
    }

    private async Task RemoveFolderAsync(string path)
    {
        await Main.RemoveMusicFolderAsync(path);
        ReloadFolders();
    }

    private void ReloadFolders()
    {
        Folders.Clear();
        foreach (var f in _s.MusicFolders) Folders.Add(new FolderRow(f, RemoveFolderAsync));
        OnPropertyChanged(nameof(HasNoFolders));
        OnPropertyChanged(nameof(LibrarySummary));
        // Скачанное по умолчанию идёт в папку с музыкой, поэтому подпись зависит от списка папок.
        OnPropertyChanged(nameof(DownloadFolder));
        OnPropertyChanged(nameof(DownloadFolderHint));
    }

    // ---------- Форматы ----------

    public List<FormatOption> Formats { get; }

    private void OnFormatsChanged()
    {
        _s.DisabledFormats = Formats.Where(f => !f.IsEnabled).SelectMany(f => f.Extensions).ToList();
        AudioFormats.SetDisabled(_s.DisabledFormats);
        _s.Save();
    }

    // ---------- Оформление ----------

    public List<ChoiceOption> Palettes { get; }
    public List<ChoiceOption> Surfaces { get; }

    [ObservableProperty] private bool _headerFromCover;

    partial void OnHeaderFromCoverChanged(bool value)
    {
        _s.HeaderFromCover = value;
        _s.Save();
    }

    private void SelectPalette(string id)
    {
        _s.Palette = id;
        // Выбрали готовую палитру — свой цвет больше не перекрывает её акцент.
        _s.CustomAccent = null;
        CustomAccentText = "";
        OnPropertyChanged(nameof(HasCustomAccent));
        _s.Save();
        MarkSelected();
        Main.ApplyTheme();
    }

    private void SelectSurface(string id)
    {
        _s.Surface = id;
        _s.Save();
        MarkSelected();
        Main.ApplyTheme();
    }

    private void MarkSelected()
    {
        foreach (var p in Palettes) p.IsSelected = p.Id == _s.Palette;
        foreach (var s in Surfaces) s.IsSelected = s.Id == _s.Surface;
    }

    // ---------- Воспроизведение ----------

    public sealed record OutputOption(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    public List<OutputOption> Outputs { get; } =
    [
        new("wasapi", "WASAPI (рекомендуется)"),
        new("waveout", "WaveOut (совместимость со старыми драйверами)"),
    ];

    [ObservableProperty] private OutputOption _selectedOutput;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatencyText))]
    private double _latency;

    public string LatencyText => $"{Latency:0} мс";

    partial void OnSelectedOutputChanged(OutputOption value)
    {
        _s.Output = value.Id;
        if (Main.Engine is NAudioEngine e) e.Output = value.Id;
        _s.Save();
    }

    partial void OnLatencyChanged(double value)
    {
        _s.LatencyMs = (int)Math.Round(value);
        if (Main.Engine is NAudioEngine e) e.LatencyMs = _s.LatencyMs;
        _s.Save();
    }

    // ---------- Горячие клавиши и о программе ----------

    public List<HotkeyRow> Hotkeys { get; } =
    [
        new("Пробел", "Играть / пауза"),
        new("Z / B", "Предыдущий / следующий трек"),
        new("X / C / V", "Играть / пауза / стоп, как в Winamp"),
        new("← / →", "Перемотка на 5 секунд"),
        new("↑ / ↓", "Громкость"),
        new("S / R", "Случайный порядок / повтор"),
        new("L", "Добавить играющий трек в любимые"),
        new("E", "Эквалайзер"),
        new("Ctrl+F", "Поиск"),
        new("Esc", "Закрыть «Сейчас играет», эквалайзер, настройки"),
        new("Alt+← / Alt+→", "Назад / вперёд"),
    ];

    public string DataFolder => AppPaths.DataDirectory;

    public List<DataModeOption> DataModes => BuildDataModes();

    private List<DataModeOption> BuildDataModes()
    {
        var mode = AppPaths.Mode;
        Task Select(DataMode m) => Main.ChangeDataLocationAsync(m);
        return
        [
            new(DataMode.AppData, "В профиле Windows",
                "%APPDATA%\\HaPlayer", mode == DataMode.AppData, Select),
            new(DataMode.Portable, "Рядом с программой (портативно)",
                "Папка «HaPlayer Data» возле HaPlayer.exe, можно носить на флешке",
                mode == DataMode.Portable, Select),
            new(DataMode.Custom, "В своей папке",
                "Например, на диске с музыкой", mode == DataMode.Custom, Select),
        ];
    }

    public string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.1.0";

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDirectory}\"") { UseShellExecute = true });
        }
        catch
        {
            // проводник не запустился: путь всё равно виден на странице
        }
    }
}
