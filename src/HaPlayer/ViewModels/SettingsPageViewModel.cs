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
    public string Status => Exists ? "" : L.T("папка не найдена");
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
    public string ButtonText => IsCurrent ? L.T("Сейчас") : Mode == DataMode.Custom ? L.T("Выбрать папку") : L.T("Перейти");
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
        ? L.T("Выбрана вручную")
        : Main.Settings.MusicFolders.Any(Directory.Exists)
            ? L.T("Ваша папка с музыкой")
            : L.T("Папок с музыкой пока нет, поэтому сюда");

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
    public string FooterText => L.F("HaPlayer {0} · без телеметрии · лицензия не выбрана", Version);

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

    // ---------- Язык ----------

    public List<string> Languages { get; } = ["English", "Русский"];

    public int LanguageIndex
    {
        get => _s.Language == "ru" ? 1 : 0;
        set
        {
            var lang = value == 1 ? "ru" : "en";
            if (lang == _s.Language) return;
            _s.Language = lang;
            _s.Save();
            OnPropertyChanged();
            // Строки переводятся при создании окна, поэтому новый язык включается перезапуском.
            Main.RestartRequested?.Invoke();
        }
    }

    // ---------- Масштаб и шрифт ----------

    private static readonly double[] Scales = [0.85, 1, 1.15, 1.3];

    public List<string> ScaleOptions { get; } = [.. Scales.Select(s => $"{s * 100:0}%")];

    public int ScaleIndex
    {
        get
        {
            var i = Array.FindIndex(Scales, s => Math.Abs(s - _s.UiScale) < 0.01);
            return i < 0 ? 1 : i;
        }
        set
        {
            if (value < 0 || value >= Scales.Length) return;
            _s.UiScale = Scales[value];
            _s.Save();
            OnPropertyChanged();
            Main.ScaleChanged?.Invoke(_s.UiScale);
        }
    }

    public List<string> FontOptions { get; } = [.. ThemeService.Fonts];

    public int FontIndex
    {
        get
        {
            var i = FontOptions.IndexOf(_s.UiFont);
            return i < 0 ? 0 : i;
        }
        set
        {
            if (value < 0 || value >= FontOptions.Count) return;
            _s.UiFont = FontOptions[value];
            _s.Save();
            OnPropertyChanged();
            ThemeService.ApplyFont(_s.UiFont);
        }
    }

    // ---------- Разделы главной ----------

    public sealed partial class HomeSection(string id, string name, SettingsPageViewModel owner) : ObservableObject
    {
        public string Id { get; } = id;
        public string Name { get; } = name;

        public bool IsShown
        {
            get => !owner._s.HomeHidden.Contains(Id);
            set
            {
                owner._s.HomeHidden.Remove(Id);
                if (!value) owner._s.HomeHidden.Add(Id);
                owner._s.Save();
                OnPropertyChanged();
            }
        }

        [RelayCommand] private void Up() => owner.MoveSection(Id, -1);
        [RelayCommand] private void Down() => owner.MoveSection(Id, 1);
    }

    private static readonly (string Id, string Name)[] SectionNames =
        [("quick", "Быстрый доступ"), ("smart", "Подборки"), ("recent", "Недавно добавленные")];

    public List<HomeSection> HomeSections
    {
        get
        {
            var order = _s.HomeOrder.Where(id => SectionNames.Any(n => n.Id == id)).ToList();
            foreach (var n in SectionNames) if (!order.Contains(n.Id)) order.Add(n.Id);
            _s.HomeOrder = order;
            return order.Select(id => new HomeSection(id, L.T(SectionNames.First(n => n.Id == id).Name), this)).ToList();
        }
    }

    private void MoveSection(string id, int delta)
    {
        var order = _s.HomeOrder;
        var i = order.IndexOf(id);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= order.Count) return;
        (order[i], order[j]) = (order[j], order[i]);
        _s.Save();
        OnPropertyChanged(nameof(HomeSections));
    }

    // ---------- Плотность и скругления ----------

    public List<string> DensityOptions { get; } = [L.T("Обычная"), L.T("Плотная")];

    public int DensityIndex
    {
        get => _s.Dense ? 1 : 0;
        set
        {
            _s.Dense = value == 1;
            _s.Save();
            OnPropertyChanged();
            Main.DensityChanged?.Invoke(_s.Dense);
            Main.ApplyListOptions();
        }
    }

    private static readonly double[] Radii = [1, 0.5, 0];

    public List<string> RadiusOptions { get; } = [L.T("Обычные"), L.T("Меньше"), L.T("Без скруглений")];

    public int RadiusIndex
    {
        get
        {
            var i = Array.FindIndex(Radii, r => Math.Abs(r - _s.UiRadius) < 0.01);
            return i < 0 ? 0 : i;
        }
        set
        {
            if (value < 0 || value >= Radii.Length) return;
            _s.UiRadius = Radii[value];
            _s.Save();
            OnPropertyChanged();
            ThemeService.RadiusFactor = _s.UiRadius;
            Main.ApplyTheme();
        }
    }

    // ---------- Опции 1.6 ----------

    /// <summary>Сохраняет флаг настроек и сообщает разметке. after вызывается уже с новым значением.</summary>
    private void SetFlag(bool value, Action<bool> set, Action? after = null, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        set(value);
        _s.Save();
        OnPropertyChanged(name);
        after?.Invoke();
    }

    public bool CompactList
    {
        get => _s.CompactList;
        set => SetFlag(value, v => _s.CompactList = v, Main.ApplyListOptions);
    }

    public bool ShowTechInfo
    {
        get => _s.ShowTechInfo;
        set => SetFlag(value, v => _s.ShowTechInfo = v, Main.ApplyListOptions);
    }

    public bool ShowPlayCount
    {
        get => _s.ShowPlayCount;
        set => SetFlag(value, v => _s.ShowPlayCount = v, Main.ApplyListOptions);
    }

    public bool TrackToast
    {
        get => _s.TrackToast;
        set => SetFlag(value, v => _s.TrackToast = v);
    }

    private static readonly string[] Corners = ["bottomright", "topright", "bottomleft", "topleft"];

    public List<string> CornerOptions { get; } = [L.T("Справа внизу"), L.T("Справа вверху"), L.T("Слева внизу"), L.T("Слева вверху")];

    public int CornerIndex
    {
        get => Math.Max(0, Array.IndexOf(Corners, _s.TrackToastCorner));
        set
        {
            if (value < 0 || value >= Corners.Length) return;
            _s.TrackToastCorner = Corners[value];
            _s.Save();
            OnPropertyChanged();
        }
    }

    public bool ShowVisualizer
    {
        get => _s.ShowVisualizer;
        set => SetFlag(value, v => _s.ShowVisualizer = v, Main.NotifyVisualizer);
    }

    private static readonly double[] Crossfades = [0, 2, 4, 6, 8, 12];

    public List<string> CrossfadeOptions { get; } =
        [L.T("Без паузы"), .. Crossfades.Skip(1).Select(s => L.F("Плавно, {0} с", s))];

    public int CrossfadeIndex
    {
        get
        {
            var i = Array.FindIndex(Crossfades, c => Math.Abs(c - _s.CrossfadeSeconds) < 0.01);
            return i < 0 ? 0 : i;
        }
        set
        {
            if (value < 0 || value >= Crossfades.Length) return;
            Main.Player.CrossfadeSeconds = Crossfades[value];
            _s.Save();
            OnPropertyChanged();
        }
    }

    public string BlacklistText => L.Plural(Main.BlacklistCount, "трек", "трека", "треков", "track", "tracks");
    public bool HasBlacklist => Main.BlacklistCount > 0;

    [RelayCommand]
    private void ClearBlacklist()
    {
        Main.ClearBlacklist();
        OnPropertyChanged(nameof(BlacklistText));
        OnPropertyChanged(nameof(HasBlacklist));
    }

    public bool ShowRatings
    {
        get => _s.ShowRatings;
        set => SetFlag(value, v => _s.ShowRatings = v, Main.ApplyListOptions);
    }

    public bool StrictTags
    {
        get => _s.StrictTags;
        set => SetFlag(value, v => _s.StrictTags = v, Main.ApplyStrictTags);
    }

    public bool RestoreQueue
    {
        get => _s.RestoreQueue;
        set => SetFlag(value, v => _s.RestoreQueue = v);
    }

    public bool GlobalHotkeys
    {
        get => _s.GlobalHotkeys;
        set => SetFlag(value, v => _s.GlobalHotkeys = v, () => Main.SetGlobalHotkeys(value));
    }

    public List<HotkeyRow> GlobalHotkeyRows { get; } =
    [
        new(L.T("Медиаклавиши"), L.T("Играть / пауза, трек назад / вперёд, стоп")),
        new(L.T("Ctrl+Alt+Пробел"), L.T("Играть / пауза")),
        new("Ctrl+Alt+← / →", L.T("Предыдущий / следующий трек")),
        new("Ctrl+Alt+↑ / ↓", L.T("Громкость")),
    ];

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
        UpdateStatus = L.T("Проверяю…");
        var release = await UpdateService.CheckAsync();
        UpdateStatus = release == null ? L.F("У вас последняя версия ({0})", Version) : L.F("Есть версия {0}", release.Version);
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
    public string LibrarySummary => L.F("В медиатеке {0}, альбомов: {1}, исполнителей: {2}.", Format.Tracks(Main.Library.Count), Main.Albums.Count, Main.Artists.Count);

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
        new("wasapi", L.T("WASAPI (рекомендуется)")),
        new("waveout", L.T("WaveOut (совместимость со старыми драйверами)")),
    ];

    [ObservableProperty] private OutputOption _selectedOutput;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatencyText))]
    private double _latency;

    public string LatencyText => L.F("{0:0} мс", Latency);

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
        new(L.T("Пробел"), L.T("Играть / пауза")),
        new("Z / B", L.T("Предыдущий / следующий трек")),
        new("X / C / V", L.T("Играть / пауза / стоп, как в Winamp")),
        new("← / →", L.T("Перемотка на 5 секунд")),
        new("↑ / ↓", L.T("Громкость")),
        new("S / R", L.T("Случайный порядок / повтор")),
        new("L", L.T("Добавить играющий трек в любимые")),
        new("E", L.T("Эквалайзер")),
        new("Ctrl+F", L.T("Поиск")),
        new("Esc", L.T("Закрыть «Сейчас играет», эквалайзер, настройки")),
        new("Alt+← / Alt+→", L.T("Назад / вперёд")),
    ];

    public string DataFolder => AppPaths.DataDirectory;

    public List<DataModeOption> DataModes => BuildDataModes();

    private List<DataModeOption> BuildDataModes()
    {
        var mode = AppPaths.Mode;
        Task Select(DataMode m) => Main.ChangeDataLocationAsync(m);
        return
        [
            new(DataMode.AppData, L.T("В профиле Windows"),
                "%APPDATA%\\HaPlayer", mode == DataMode.AppData, Select),
            new(DataMode.Portable, L.T("Рядом с программой (портативно)"),
                L.T("Папка «HaPlayer Data» возле HaPlayer.exe, можно носить на флешке"),
                mode == DataMode.Portable, Select),
            new(DataMode.Custom, L.T("В своей папке"),
                L.T("Например, на диске с музыкой"), mode == DataMode.Custom, Select),
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
