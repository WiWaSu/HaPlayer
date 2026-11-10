using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace HaPlayer.Ui;

/// <summary>
/// Значок в сетке 24×24, нарисованный тонкой линией со скруглёнными концами, как в Spotify.
/// Значки из Icons.Solid (играть, пауза, сердце и т.п.) заливаются целиком.
/// </summary>
public sealed class Glyph : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Glyph, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Glyph>();

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Glyph, double>(nameof(StrokeThickness), 2);

    static Glyph()
    {
        AffectsRender<Glyph>(DataProperty, ForegroundProperty, StrokeThicknessProperty);
        AffectsMeasure<Glyph>(DataProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>Толщина линии в единицах сетки 24×24: при размере 24 px это пиксели.</summary>
    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsNaN(Width) ? 24 : Width;
        var h = double.IsNaN(Height) ? 24 : Height;
        return new Size(w, h);
    }

    public override void Render(DrawingContext context)
    {
        if (Data is not { } data || Foreground is not { } brush) return;

        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 0) return;
        var scale = side / 24;
        var dx = (Bounds.Width - side) / 2;
        var dy = (Bounds.Height - side) / 2;

        var solid = Icons.IsSolid(data);
        var pen = new Pen(brush, solid ? 1.5 : StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy)))
        {
            // Заливка вместе с обводкой той же кистью даёт залитую фигуру со скруглёнными углами.
            context.DrawGeometry(solid ? brush : null, pen, data);
        }
    }
}
