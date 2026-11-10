using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlayer.Audio;
using HaPlayer.Library;

namespace HaPlayer.ViewModels;

public sealed partial class EqBand(string label, Action changed) : ObservableObject
{
    public string Label { get; } = label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainText))]
    private double _gain;

    public string GainText => Gain switch
    {
        > 0.05 => $"+{Gain:0.#}",
        < -0.05 => $"{Gain:0.#}",
        _ => "0",
    };

    partial void OnGainChanged(double value) => changed();
}

public sealed partial class EqualizerViewModel : ObservableObject
{
    /// <summary>Пресеты в дБ для полос 31 Гц … 16 кГц.</summary>
    private static readonly Dictionary<string, double[]> PresetGains = new()
    {
        ["Плоский"] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        ["Больше баса"] = [6, 5, 4, 2, 0, 0, 0, 0, 0, 0],
        ["Рок"] = [5, 4, 3, 1, -1, -1, 1, 3, 4, 5],
        ["Поп"] = [-1, 1, 3, 4, 3, 0, -1, -1, 1, 2],
        ["Электроника"] = [5, 4, 1, 0, -2, 1, 0, 1, 4, 5],
        ["Хип-хоп"] = [5, 4, 1, 3, -1, -1, 1, -1, 2, 3],
        ["Джаз"] = [3, 2, 1, 2, -1, -1, 0, 1, 2, 3],
        ["Классика"] = [4, 3, 2, 1, -1, -1, 0, 2, 3, 4],
        ["Вокал"] = [-2, -1, 0, 2, 4, 4, 3, 1, 0, -1],
        ["Больше высоких"] = [0, 0, 0, 0, 0, 1, 2, 4, 5, 6],
    };

    private const string Custom = "Свой";

    private readonly IAudioEngine _engine;
    private readonly Settings _settings;
    private bool _applyingPreset;

    public EqualizerViewModel(IAudioEngine engine, Settings settings)
    {
        _engine = engine;
        _settings = settings;

        string[] labels = ["31", "62", "125", "250", "500", "1K", "2K", "4K", "8K", "16K"];
        _applyingPreset = true;
        for (var i = 0; i < labels.Length; i++)
        {
            var band = new EqBand(labels[i], OnBandChanged)
            {
                Gain = i < settings.EqGains.Length ? settings.EqGains[i] : 0,
            };
            Bands.Add(band);
        }
        _applyingPreset = false;

        Presets = [.. PresetGains.Keys, Custom];
        _enabled = settings.EqEnabled;
        _preamp = settings.EqPreamp;
        _selectedPreset = Presets.Contains(settings.EqPreset) ? settings.EqPreset : Custom;
        Apply();
    }

    public ObservableCollection<EqBand> Bands { get; } = [];
    public List<string> Presets { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BandsOpacity))]
    private bool _enabled;

    /// <summary>Выключенный эквалайзер показываем приглушённым, но ползунки оставляем доступными.</summary>
    public double BandsOpacity => Enabled ? 1 : 0.5;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreampText))]
    private double _preamp;

    [ObservableProperty] private string _selectedPreset;

    public string PreampText => Preamp > 0.05 ? $"+{Preamp:0.#}" : Preamp < -0.05 ? $"{Preamp:0.#}" : "0";

    partial void OnEnabledChanged(bool value) => Apply();
    partial void OnPreampChanged(double value) => Apply();

    partial void OnSelectedPresetChanged(string value)
    {
        if (!PresetGains.TryGetValue(value, out var gains)) return;
        _applyingPreset = true;
        for (var i = 0; i < Bands.Count; i++) Bands[i].Gain = gains[i];
        _applyingPreset = false;
        // Выбрал пресет — значит хочет его слышать.
        if (!Enabled && value != "Плоский") Enabled = true;
        Apply();
    }

    private void OnBandChanged()
    {
        if (_applyingPreset) return;
        if (SelectedPreset != Custom)
        {
            _selectedPreset = Custom;
            OnPropertyChanged(nameof(SelectedPreset));
        }
        Apply();
    }

    [RelayCommand]
    private void Reset()
    {
        Preamp = 0;
        SelectedPreset = "Плоский";
    }

    private void Apply()
    {
        var gains = Bands.Select(b => (float)b.Gain).ToArray();
        _engine.SetEqualizer(new EqualizerSettings(Enabled, (float)Preamp, gains));
        _settings.EqEnabled = Enabled;
        _settings.EqPreamp = Preamp;
        _settings.EqGains = Bands.Select(b => b.Gain).ToArray();
        _settings.EqPreset = SelectedPreset;
    }
}
