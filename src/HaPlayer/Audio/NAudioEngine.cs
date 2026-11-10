using NAudio.CoreAudioApi;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HaPlayer.Audio;

/// <summary>
/// Реализация для Windows. Декодирует через Media Foundation (MP3, AAC/M4A, FLAC, ALAC, WAV, WMA)
/// и NVorbis (Ogg Vorbis), выводит через WASAPI в общем режиме.
/// Остальные форматы (Opus, APE, WavPack) появятся вместе с декодером на FFmpeg.
/// </summary>
public sealed class NAudioEngine : IAudioEngine
{
    private IWavePlayer? _output;
    private WaveStream? _reader;
    private VolumeSampleProvider? _volumeProvider;
    private Equalizer? _equalizer;
    private EqualizerSettings _eqSettings = EqualizerSettings.Off;
    private float _volume = 1f;
    private bool _manualStop;

    public event EventHandler? TrackEnded;
    public event EventHandler<Exception>? PlaybackFailed;

    public void Open(string path)
    {
        Close();

        WaveStream reader = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".ogg" or ".oga" => new VorbisWaveReader(path),
            _ => new MediaFoundationReader(path),
        };

        try
        {
            // Цепочка: декодер → эквалайзер → громкость → вывод.
            _equalizer = new Equalizer(reader.ToSampleProvider(), _eqSettings);
            _volumeProvider = new VolumeSampleProvider(_equalizer) { Volume = _volume };
            _output = CreateOutput(_volumeProvider);
            _reader = reader;
        }
        catch
        {
            reader.Dispose();
            _volumeProvider = null;
            throw;
        }
    }

    /// <summary>Способ вывода: "wasapi" или "waveout". Применяется со следующего трека.</summary>
    public string Output { get; set; } = "wasapi";

    /// <summary>Размер буфера вывода в мс. Меньше — быстрее реакция, больше — устойчивее на слабом ПК.</summary>
    public int LatencyMs { get; set; } = 100;

    /// <summary>Выбранное устройство вывода (id WASAPI). null — устройство Windows по умолчанию.</summary>
    public string? DeviceId { get; private set; }

    public IReadOnlyList<AudioDevice> GetDevices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            try
            {
                defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
            }
            catch
            {
                // устройств вывода нет совсем
            }
            return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(d => new AudioDevice(d.ID, d.FriendlyName, d.ID == defaultId, AudioDevice.GuessKind(d.FriendlyName)))
                .OrderByDescending(d => d.IsDefault)
                .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Переключает устройство вывода. Если трек играет, он продолжит играть с того же места.</summary>
    public void SetDevice(string? id)
    {
        DeviceId = id;
        if (_output == null || _volumeProvider == null) return;

        var wasPlaying = IsPlaying;
        var old = _output;
        old.PlaybackStopped -= OnPlaybackStopped;
        old.Stop();
        old.Dispose();
        _output = CreateOutput(_volumeProvider);
        if (wasPlaying) _output.Play();
    }

    private MMDevice? ResolveDevice()
    {
        if (DeviceId == null) return null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var d = enumerator.GetDevice(DeviceId);
            return d.State == DeviceState.Active ? d : null;
        }
        catch
        {
            // устройство отключили: играем на устройстве по умолчанию
            return null;
        }
    }

    private IWavePlayer CreateOutput(ISampleProvider source)
    {
        // WASAPI даёт меньшую задержку и позже позволит включить режим exclusive (bit-perfect).
        // Если устройство его не приняло, откатываемся на WaveOut, он есть везде.
        var latency = Math.Clamp(LatencyMs, 30, 1000);
        if (Output != "waveout")
        {
            IWavePlayer? wasapi = null;
            try
            {
                var device = ResolveDevice();
                wasapi = device != null
                    ? new WasapiOut(device, AudioClientShareMode.Shared, true, latency)
                    : new WasapiOut(AudioClientShareMode.Shared, true, latency);
                wasapi.Init(source);
                wasapi.PlaybackStopped += OnPlaybackStopped;
                return wasapi;
            }
            catch
            {
                wasapi?.Dispose();
            }
        }

        var waveOut = new WaveOutEvent { DesiredLatency = Math.Max(latency, 60) };
        waveOut.Init(source);
        waveOut.PlaybackStopped += OnPlaybackStopped;
        return waveOut;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (!ReferenceEquals(sender, _output)) return;
        if (_manualStop)
        {
            _manualStop = false;
            return;
        }
        if (e.Exception != null) PlaybackFailed?.Invoke(this, e.Exception);
        else TrackEnded?.Invoke(this, EventArgs.Empty);
    }

    public void Play() => _output?.Play();

    public void Pause() => _output?.Pause();

    public void Stop()
    {
        if (_output == null) return;
        if (_output.PlaybackState != PlaybackState.Stopped)
        {
            _manualStop = true;
            _output.Stop();
        }
        if (_reader != null) _reader.Position = 0;
    }

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    public TimeSpan Position
    {
        get => _reader?.CurrentTime ?? TimeSpan.Zero;
        set
        {
            if (_reader == null) return;
            var max = _reader.TotalTime;
            if (value < TimeSpan.Zero) value = TimeSpan.Zero;
            if (value > max) value = max;
            _reader.CurrentTime = value;
        }
    }

    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_volumeProvider != null) _volumeProvider.Volume = _volume;
        }
    }

    public void SetEqualizer(EqualizerSettings settings)
    {
        _eqSettings = settings;
        _equalizer?.Update(settings);
    }

    private void Close()
    {
        _equalizer = null;
        if (_output != null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            _output.Stop();
            _output.Dispose();
            _output = null;
        }
        _reader?.Dispose();
        _reader = null;
        _volumeProvider = null;
        _manualStop = false;
    }

    public void Dispose() => Close();
}
