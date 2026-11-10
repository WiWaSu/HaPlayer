using NAudio.Dsp;
using NAudio.Wave;

namespace HaPlayer.Audio;

/// <summary>Настройки эквалайзера: включён ли, предусиление и усиление по полосам в дБ.</summary>
public sealed record EqualizerSettings(bool Enabled, float PreampDb, float[] GainsDb)
{
    /// <summary>Центральные частоты полос, Гц. Шаг в октаву, как в классических 10-полосных эквалайзерах.</summary>
    public static readonly float[] Frequencies = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    public static EqualizerSettings Off { get; } = new(false, 0, new float[Frequencies.Length]);
}

/// <summary>
/// 10-полосный эквалайзер на пиковых biquad-фильтрах. Стоит в цепочке между декодером и громкостью.
/// Новые настройки подхватываются в аудиопотоке на следующем блоке, поэтому ползунки можно двигать на ходу.
/// </summary>
public sealed class Equalizer : ISampleProvider
{
    private const float Q = 1.1f;

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly float _sampleRate;
    private EqualizerSettings? _pending;

    private bool _enabled;
    private float _preamp = 1f;
    // [канал][активная полоса]. Полосы с нулевым усилением не создаются, чтобы не тратить процессор.
    private BiQuadFilter[][] _filters = [];

    public Equalizer(ISampleProvider source, EqualizerSettings settings)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        _sampleRate = source.WaveFormat.SampleRate;
        Apply(settings);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Вызывается из UI-потока. Применится в аудиопотоке перед следующим блоком.</summary>
    public void Update(EqualizerSettings settings) => Volatile.Write(ref _pending, settings);

    private void Apply(EqualizerSettings s)
    {
        _enabled = s.Enabled;
        _preamp = (float)Math.Pow(10, s.PreampDb / 20);
        var nyquist = _sampleRate / 2;
        var bands = new List<(float Freq, float Gain)>();
        for (var i = 0; i < EqualizerSettings.Frequencies.Length && i < s.GainsDb.Length; i++)
        {
            var f = EqualizerSettings.Frequencies[i];
            if (Math.Abs(s.GainsDb[i]) > 0.05f && f < nyquist * 0.9f) bands.Add((f, s.GainsDb[i]));
        }

        var filters = new BiQuadFilter[_channels][];
        for (var ch = 0; ch < _channels; ch++)
            filters[ch] = bands.Select(b => BiQuadFilter.PeakingEQ(_sampleRate, b.Freq, Q, b.Gain)).ToArray();
        _filters = filters;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);

        var pending = Interlocked.Exchange(ref _pending, null);
        if (pending != null) Apply(pending);
        if (!_enabled || read == 0) return read;

        var filters = _filters;
        var pre = _preamp;
        for (var i = 0; i < read; i++)
        {
            var bandFilters = filters[i % _channels];
            var x = buffer[offset + i] * pre;
            for (var b = 0; b < bandFilters.Length; b++) x = bandFilters[b].Transform(x);
            // Жёсткое ограничение, чтобы подъём полос не давал переполнения. Для громких подъёмов уменьшите предусиление.
            buffer[offset + i] = x > 1f ? 1f : x < -1f ? -1f : x;
        }
        return read;
    }
}
