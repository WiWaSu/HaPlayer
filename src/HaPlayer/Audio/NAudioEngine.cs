using NAudio.CoreAudioApi;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HaPlayer.Audio;

/// <summary>
/// Реализация для Windows. Декодирует через Media Foundation (MP3, AAC/M4A, FLAC, ALAC, WAV, WMA)
/// и NVorbis (Ogg Vorbis), выводит через WASAPI в общем режиме.
///
/// Вывод открывается один раз и работает в постоянном формате 48 кГц стерео. Треки играет «дека»:
/// она заранее открывает следующий трек и переходит на него без паузы или с плавным переходом.
/// Цепочка: дека → эквалайзер → отвод для визуализатора → громкость → вывод.
/// </summary>
public sealed class NAudioEngine : IAudioEngine
{
    private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    private readonly Deck _deck;
    private readonly Equalizer _equalizer;
    private readonly SpectrumTap _tap;
    private readonly VolumeSampleProvider _volumeProvider;
    private IWavePlayer? _output;
    private bool _outputDirty;
    private bool _manualStop;

    public NAudioEngine()
    {
        _deck = new Deck(MixFormat);
        _deck.Ended += () => TrackEnded?.Invoke(this, EventArgs.Empty);
        _deck.Advanced += path => AdvancedToNext?.Invoke(this, path);
        _equalizer = new Equalizer(_deck, EqualizerSettings.Off);
        _tap = new SpectrumTap(_equalizer);
        _volumeProvider = new VolumeSampleProvider(_tap) { Volume = 1f };
    }

    public event EventHandler? TrackEnded;
    public event EventHandler<Exception>? PlaybackFailed;
    public event EventHandler<string>? AdvancedToNext;

    public void Open(string path)
    {
        var source = TrackSource.Open(path, MixFormat);
        if (_outputDirty) RecreateOutput(false);
        _deck.SetCurrent(source);
        _manualStop = false;
    }

    public void SetNext(string? path)
    {
        if (path == null)
        {
            _deck.SetNext(null);
            return;
        }
        if (_deck.NextPath == path) return;
        try
        {
            _deck.SetNext(TrackSource.Open(path, MixFormat));
        }
        catch
        {
            // Следующий файл не открылся: переход будет обычным, через TrackEnded.
            _deck.SetNext(null);
        }
    }

    public double CrossfadeSeconds
    {
        get => _deck.CrossfadeSeconds;
        set => _deck.CrossfadeSeconds = Math.Clamp(value, 0, 12);
    }

    public int SampleRate => MixFormat.SampleRate;

    public void CopyRecentSamples(float[] dest) => _tap.CopyRecent(dest);

    private string _outputMode = "wasapi";
    private int _latencyMs = 100;

    /// <summary>Способ вывода: "wasapi" или "waveout". Применяется со следующего трека.</summary>
    public string Output
    {
        get => _outputMode;
        set
        {
            if (_outputMode == value) return;
            _outputMode = value;
            _outputDirty = true;
        }
    }

    /// <summary>Размер буфера вывода в мс. Меньше — быстрее реакция, больше — устойчивее на слабом ПК.</summary>
    public int LatencyMs
    {
        get => _latencyMs;
        set
        {
            if (_latencyMs == value) return;
            _latencyMs = value;
            _outputDirty = true;
        }
    }

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
        if (_output != null) RecreateOutput(IsPlaying);
    }

    private void RecreateOutput(bool play)
    {
        if (_output != null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            _output.Stop();
            _output.Dispose();
            _output = null;
        }
        _outputDirty = false;
        _output = CreateOutput(_volumeProvider);
        if (play) _output.Play();
    }

    private IWavePlayer EnsureOutput()
    {
        if (_output == null || _outputDirty) RecreateOutput(false);
        return _output!;
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
        // WASAPI даёт меньшую задержку. Если устройство его не приняло, откатываемся на WaveOut, он есть везде.
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
        // Вывод работает непрерывно, сам он останавливается только из-за ошибки (например, отключили наушники).
        if (e.Exception != null) PlaybackFailed?.Invoke(this, e.Exception);
    }

    public void Play()
    {
        if (!_deck.HasCurrent) return;
        _deck.Paused = false;
        var output = EnsureOutput();
        if (output.PlaybackState != PlaybackState.Playing) output.Play();
    }

    public void Pause()
    {
            _deck.Paused = true;
        if (_output != null && _output.PlaybackState == PlaybackState.Playing) _output.Pause();
    }

    public void Stop()
    {
        Pause();
        _deck.Rewind();
    }

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing && !_deck.Paused && _deck.HasCurrent;

    public TimeSpan Position
    {
        get => _deck.Position;
        set => _deck.Position = value;
    }

    public TimeSpan Duration => _deck.Duration;

    public float Volume
    {
        get => _volumeProvider.Volume;
        set => _volumeProvider.Volume = Math.Clamp(value, 0f, 1f);
    }

    public void SetEqualizer(EqualizerSettings settings) => _equalizer.Update(settings);

    public void Dispose()
    {
        if (_output != null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            _output.Stop();
            _output.Dispose();
            _output = null;
        }
        _deck.Dispose();
    }
}

/// <summary>Открытый файл, приведённый к формату вывода (48 кГц, стерео, float).</summary>
internal sealed class TrackSource : IDisposable
{
    private readonly WaveStream _reader;
    private readonly ISampleProvider _samples;

    private TrackSource(string path, WaveStream reader, ISampleProvider samples)
    {
        Path = path;
        _reader = reader;
        _samples = samples;
    }

    public string Path { get; }

    public static TrackSource Open(string path, WaveFormat target)
    {
        WaveStream reader = System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".ogg" or ".oga" => new VorbisWaveReader(path),
            _ => new MediaFoundationReader(path),
        };
        try
        {
            var s = reader.ToSampleProvider();
            if (s.WaveFormat.Channels == 1) s = new MonoToStereoSampleProvider(s);
            else if (s.WaveFormat.Channels > 2) s = new FirstTwoChannels(s);
            if (s.WaveFormat.SampleRate != target.SampleRate) s = new WdlResamplingSampleProvider(s, target.SampleRate);
            return new TrackSource(path, reader, s);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        try
        {
            return _samples.Read(buffer, offset, count);
        }
        catch
        {
            // Битый кусок файла: считаем, что трек кончился.
            return 0;
        }
    }

    public TimeSpan Position
    {
        get => _reader.CurrentTime;
        set
        {
            if (value < TimeSpan.Zero) value = TimeSpan.Zero;
            if (value > _reader.TotalTime) value = _reader.TotalTime;
            _reader.CurrentTime = value;
        }
    }

    public TimeSpan Duration => _reader.TotalTime;

    public TimeSpan Remaining => Duration - Position;

    public void Dispose() => _reader.Dispose();

    /// <summary>Многоканальный файл (5.1 и т. п.): берём левый и правый каналы.</summary>
    private sealed class FirstTwoChannels(ISampleProvider source) : ISampleProvider
    {
        private readonly int _channels = source.WaveFormat.Channels;
        private float[] _buffer = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            var frames = count / 2;
            var need = frames * _channels;
            if (_buffer.Length < need) _buffer = new float[need];
            var read = source.Read(_buffer, 0, need) / _channels;
            for (var i = 0; i < read; i++)
            {
                buffer[offset + i * 2] = _buffer[i * _channels];
                buffer[offset + i * 2 + 1] = _buffer[i * _channels + 1];
            }
            return read * 2;
        }
    }
}

/// <summary>
/// Играет текущий трек и держит наготове следующий. Когда текущий кончается, сразу продолжает следующим
/// в том же блоке звука, поэтому между треками нет паузы. Если включён плавный переход, следующий трек
/// начинает звучать за N секунд до конца текущего: один затихает, другой нарастает.
/// </summary>
internal sealed class Deck(WaveFormat format) : ISampleProvider, IDisposable
{
    private readonly object _lock = new();
    private TrackSource? _current;
    private TrackSource? _next;
    private bool _mixing;
    private float[] _mixBuffer = [];
    private bool _endReported;

    public WaveFormat WaveFormat { get; } = format;

    public double CrossfadeSeconds { get; set; }

    public volatile bool Paused;

    /// <summary>Трек кончился, а следующего нет. Приходит из аудиопотока.</summary>
    public event Action? Ended;

    /// <summary>Дека перешла на заранее открытый трек (путь к нему). Приходит из аудиопотока.</summary>
    public event Action<string>? Advanced;

    public bool HasCurrent
    {
        get { lock (_lock) return _current != null; }
    }

    public string? NextPath
    {
        get { lock (_lock) return _next?.Path; }
    }

    public void SetCurrent(TrackSource source)
    {
        lock (_lock)
        {
            _current?.Dispose();
            _next?.Dispose();
            _current = source;
            _next = null;
            _mixing = false;
            _endReported = false;
        }
    }

    public void SetNext(TrackSource? source)
    {
        lock (_lock)
        {
            if (_mixing)
            {
                // Переход уже идёт: менять входящий трек на лету нельзя, новый выбор дождётся следующего раза.
                source?.Dispose();
                return;
            }
            _next?.Dispose();
            _next = source;
        }
    }

    public void Rewind()
    {
        lock (_lock)
        {
            if (_current != null) _current.Position = TimeSpan.Zero;
            _mixing = false;
            _endReported = false;
        }
    }

    public TimeSpan Position
    {
        get { lock (_lock) return _current?.Position ?? TimeSpan.Zero; }
        set
        {
            lock (_lock)
            {
                if (_current == null) return;
                _current.Position = value;
                _endReported = false;
                if (_mixing)
                {
                    // Перемотали посреди перехода: входящий трек начнётся заново, когда снова подойдёт время.
                    _mixing = false;
                    if (_next != null) _next.Position = TimeSpan.Zero;
                }
            }
        }
    }

    public TimeSpan Duration
    {
        get { lock (_lock) return _current?.Duration ?? TimeSpan.Zero; }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        string? advancedTo = null;
        var ended = false;
        lock (_lock)
        {
            if (Paused || _current == null)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            var xfade = CrossfadeSeconds;
            if (xfade > 0.1 && _next != null && !_mixing && _current.Remaining.TotalSeconds <= xfade
                && _current.Duration.TotalSeconds > xfade * 2)
                _mixing = true;

            var read = _current.Read(buffer, offset, count);

            if (_mixing && _next != null)
            {
                // Плавный переход: текущий затихает, следующий нарастает по косинусу (без провала громкости).
                if (_mixBuffer.Length < count) _mixBuffer = new float[count];
                var nextRead = _next.Read(_mixBuffer, 0, count);
                var remaining = Math.Max(0, _current.Remaining.TotalSeconds);
                var tOut = (float)Math.Clamp(remaining / xfade, 0, 1);
                var gOut = MathF.Sin(tOut * MathF.PI / 2);
                var gIn = MathF.Cos(tOut * MathF.PI / 2);
                for (var i = 0; i < count; i++)
                {
                    var a = i < read ? buffer[offset + i] * gOut : 0f;
                    var b = i < nextRead ? _mixBuffer[i] * gIn : 0f;
                    buffer[offset + i] = a + b;
                }
                if (read < count)
                {
                    // Текущий доиграл: следующий становится текущим и продолжает с того места, где уже звучит.
                    _current.Dispose();
                    _current = _next;
                    _next = null;
                    _mixing = false;
                    advancedTo = _current.Path;
                }
                read = count;
            }
            else if (read < count)
            {
                if (_next != null)
                {
                    // Без паузы: остаток блока заполняем началом следующего трека.
                    _current.Dispose();
                    _current = _next;
                    _next = null;
                    advancedTo = _current.Path;
                    var more = _current.Read(buffer, offset + read, count - read);
                    read += more;
                }
                if (read < count)
                {
                    Array.Clear(buffer, offset + read, count - read);
                    if (advancedTo == null && !_endReported)
                    {
                        _endReported = true;
                        ended = true;
                    }
                }
            }
        }

        if (advancedTo != null) Advanced?.Invoke(advancedTo);
        if (ended) Ended?.Invoke();
        return count;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _current?.Dispose();
            _next?.Dispose();
            _current = null;
            _next = null;
        }
    }
}

/// <summary>Пропускает звук без изменений и запоминает последние 4096 отсчётов (моно) для визуализатора.</summary>
internal sealed class SpectrumTap(ISampleProvider source) : ISampleProvider
{
    private const int Size = 4096;
    private readonly float[] _ring = new float[Size];
    private int _write;

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        var ch = WaveFormat.Channels;
        var w = _write;
        for (var i = 0; i + ch - 1 < read; i += ch)
        {
            var sum = 0f;
            for (var c = 0; c < ch; c++) sum += buffer[offset + i + c];
            _ring[w] = sum / ch;
            w = (w + 1) & (Size - 1);
        }
        Volatile.Write(ref _write, w);
        return read;
    }

    /// <summary>Копирует последние dest.Length отсчётов (не больше 4096), старые первыми.</summary>
    public void CopyRecent(float[] dest)
    {
        var n = Math.Min(dest.Length, Size);
        var end = Volatile.Read(ref _write);
        var start = (end - n + Size) & (Size - 1);
        for (var i = 0; i < n; i++) dest[i] = _ring[(start + i) & (Size - 1)];
    }
}
