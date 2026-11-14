using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HaPlayer.Audio;
using NAudio.Dsp;

namespace HaPlayer.Ui;

/// <summary>
/// Визуализатор: столбики спектра того, что звучит сейчас. Берёт последние 2048 отсчётов у движка,
/// считает спектр и рисует полосы от 40 Гц до 16 кГц в логарифмической шкале.
/// Пока элемент не виден или музыка на паузе, спектр не считается.
/// </summary>
public sealed class SpectrumView : Control
{
    /// <summary>Откуда брать звук. Задаёт App при запуске.</summary>
    public static IAudioEngine? Engine { get; set; }

    public static readonly StyledProperty<int> BarsProperty =
        AvaloniaProperty.Register<SpectrumView, int>(nameof(Bars), 48);

    public static readonly StyledProperty<IBrush?> BarBrushProperty =
        AvaloniaProperty.Register<SpectrumView, IBrush?>(nameof(BarBrush));

    public int Bars
    {
        get => GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    /// <summary>Цвет столбиков. Если не задан, берётся акцент темы.</summary>
    public IBrush? BarBrush
    {
        get => GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    private const int N = 2048;
    private const int M = 11; // 2^11 = 2048
    private readonly float[] _samples = new float[N];
    private readonly Complex[] _fft = new Complex[N];
    private readonly float[] _window = new float[N];
    private float[] _levels = [];
    private bool _attached;
    private TimeSpan _last;

    public SpectrumView()
    {
        // Окно Ханна убирает «размазывание» спектра на краях блока.
        for (var i = 0; i < N; i++) _window[i] = 0.5f * (1 - MathF.Cos(2 * MathF.PI * i / (N - 1)));
        IsHitTestVisible = false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        RequestFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
    }

    private void RequestFrame() => TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);

    private void OnFrame(TimeSpan time)
    {
        if (!_attached) return;
        var dt = _last == default ? 1 / 60.0 : Math.Clamp((time - _last).TotalSeconds, 0, 0.1);
        _last = time;
        if (IsEffectivelyVisible && Bounds.Width > 0) Update((float)dt);
        RequestFrame();
    }

    private void Update(float dt)
    {
        var bars = Math.Max(4, Bars);
        if (_levels.Length != bars) _levels = new float[bars];
        var engine = Engine;
        var playing = engine is { IsPlaying: true };
        var changed = false;

        float[]? target = null;
        if (playing)
        {
            engine!.CopyRecentSamples(_samples);
            for (var i = 0; i < N; i++)
            {
                _fft[i].X = _samples[i] * _window[i];
                _fft[i].Y = 0;
            }
            FastFourierTransform.FFT(true, M, _fft);

            target = new float[bars];
            var rate = engine.SampleRate;
            var binHz = (double)rate / N;
            const double lo = 40, hi = 16000;
            for (var b = 0; b < bars; b++)
            {
                var f0 = lo * Math.Pow(hi / lo, (double)b / bars);
                var f1 = lo * Math.Pow(hi / lo, (double)(b + 1) / bars);
                var i0 = Math.Max(1, (int)(f0 / binHz));
                var i1 = Math.Max(i0 + 1, (int)(f1 / binHz));
                var peak = 0f;
                for (var i = i0; i < i1 && i < N / 2; i++)
                {
                    var mag = MathF.Sqrt(_fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y);
                    if (mag > peak) peak = mag;
                }
                // В дБ и в диапазон 0..1: тишина около -60 дБ, громкое около 0 дБ.
                var db = 20 * MathF.Log10(peak + 1e-9f);
                target[b] = Math.Clamp((db + 62) / 52, 0, 1);
            }
        }

        for (var b = 0; b < bars; b++)
        {
            var want = target?[b] ?? 0;
            var cur = _levels[b];
            // Растёт быстро, опускается плавно: так глаз успевает увидеть удар.
            var next = want > cur ? cur + (want - cur) * Math.Min(1, dt * 30) : Math.Max(want, cur - dt * 1.6f);
            if (Math.Abs(next - cur) > 0.001f) changed = true;
            _levels[b] = next;
        }
        if (changed) InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bars = _levels.Length;
        if (bars == 0) return;
        var brush = BarBrush ?? (this.TryFindResource("AccentBrush", out var r) ? r as IBrush : null) ?? Brushes.White;
        var w = Bounds.Width;
        var h = Bounds.Height;
        var slot = w / bars;
        var barW = Math.Max(1, slot * 0.62);
        var radius = Math.Min(barW / 2, 3);
        for (var b = 0; b < bars; b++)
        {
            var bh = Math.Max(2, _levels[b] * h);
            var x = b * slot + (slot - barW) / 2;
            context.DrawRectangle(brush, null, new RoundedRect(new Rect(x, h - bh, barW, bh), radius));
        }
    }
}
