using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HaPlayer.Ui;

/// <summary>
/// Анимированная абстракция для шапки страницы: несколько мягких цветных пятен в цветах обложки медленно плавают.
/// Рисуется, только пока видна на экране.
/// </summary>
public sealed class AbstractBanner : Control
{
    public static readonly StyledProperty<Color> BaseColorProperty =
        AvaloniaProperty.Register<AbstractBanner, Color>(nameof(BaseColor), Color.Parse("#535353"));

    public Color BaseColor
    {
        get => GetValue(BaseColorProperty);
        set => SetValue(BaseColorProperty, value);
    }

    private bool _attached;
    private double _t;
    private TimeSpan _last;

    // Каждое пятно: оттенок (сдвиг относительно основного цвета), размер, скорость и фаза движения.
    private static readonly (double Hue, double Light, double Size, double SpeedX, double SpeedY, double Phase)[] Blobs =
    [
        (0, 0.10, 0.75, 0.11, 0.07, 0.0),
        (40, 0.25, 0.60, 0.07, 0.13, 1.7),
        (-50, -0.05, 0.70, 0.09, 0.05, 3.1),
        (180, 0.15, 0.45, 0.05, 0.09, 4.4),
        (0, 0.40, 0.35, 0.13, 0.11, 5.9),
    ];

    public AbstractBanner()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
    }

    private void OnFrame(TimeSpan time)
    {
        if (!_attached) return;
        var dt = _last == default ? 0 : Math.Min(0.1, (time - _last).TotalSeconds);
        _last = time;
        if (IsEffectivelyVisible)
        {
            _t += dt;
            InvalidateVisual();
        }
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        var baseColor = BaseColor;
        context.FillRectangle(new SolidColorBrush(Shade(baseColor, -0.35)), new Rect(0, 0, w, h));
        var size = Math.Max(w, h);
        foreach (var b in Blobs)
        {
            var x = w * (0.5 + 0.42 * Math.Sin(_t * b.SpeedX + b.Phase));
            var y = h * (0.5 + 0.45 * Math.Cos(_t * b.SpeedY + b.Phase * 0.7));
            var r = size * b.Size * (0.9 + 0.1 * Math.Sin(_t * 0.3 + b.Phase));
            var c = Shade(RotateHue(baseColor, b.Hue), b.Light);
            var brush = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(200, c.R, c.G, c.B), 0),
                    new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1),
                },
            };
            context.DrawEllipse(brush, null, new Point(x, y), r / 2, r / 2);
        }
    }

    private static Color Shade(Color c, double amount) => amount >= 0
        ? ThemeService.Mix(c, Colors.White, amount)
        : ThemeService.Mix(c, Colors.Black, -amount);

    private static Color RotateHue(Color c, double degrees)
    {
        if (degrees == 0) return c;
        var hsl = c.ToHsl();
        return HslColor.ToRgb((hsl.H + degrees + 360) % 360, Math.Max(0.35, hsl.S), Math.Clamp(hsl.L, 0.25, 0.6));
    }
}
