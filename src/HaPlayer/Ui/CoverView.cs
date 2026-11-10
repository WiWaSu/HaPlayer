using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HaPlayer.Ui;

/// <summary>
/// Квадрат с обложкой. Пока картинки нет, показывает значок ноты.
/// С Badge рисует особую обложку: градиент бренда и значок («Любимые треки», «Все треки»).
/// </summary>
public sealed class CoverView : Border
{
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<CoverView, IImage?>(nameof(Source));

    public static readonly StyledProperty<bool> IsLikedProperty =
        AvaloniaProperty.Register<CoverView, bool>(nameof(IsLiked));

    public static readonly StyledProperty<Geometry?> BadgeProperty =
        AvaloniaProperty.Register<CoverView, Geometry?>(nameof(Badge));

    private static readonly IBrush PlaceholderBrush = new SolidColorBrush(Color.Parse("#282828"));
    private static readonly IBrush NoteBrush = new SolidColorBrush(Color.Parse("#7A7A7A"));

    private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
    private readonly Glyph _icon = new()
    {
        Data = Icons.Note,
        Foreground = NoteBrush,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };

    public CoverView()
    {
        Background = PlaceholderBrush;
        CornerRadius = new CornerRadius(4);
        ClipToBounds = true;
        Child = new Panel { Children = { _icon, _image } };
    }

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Обложка «Любимых треков». То же, что Badge = сердце.</summary>
    public bool IsLiked
    {
        get => GetValue(IsLikedProperty);
        set => SetValue(IsLikedProperty, value);
    }

    public Geometry? Badge
    {
        get => GetValue(BadgeProperty);
        set => SetValue(BadgeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == IsLikedProperty || change.Property == BadgeProperty)
        {
            var badge = Badge ?? (IsLiked ? Icons.Heart : null);
            _image.Source = badge != null ? null : Source;
            _image.IsVisible = badge == null && Source != null;
            _icon.IsVisible = badge != null || Source == null;
            _icon.Data = badge ?? Icons.Note;
            _icon.Foreground = badge != null ? Brushes.White : NoteBrush;
            Background = badge != null ? ThemeService.LikedBrush() : PlaceholderBrush;
        }
        else if (change.Property == BoundsProperty)
        {
            var side = Math.Max(12, Math.Min(Bounds.Width, Bounds.Height) * 0.42);
            _icon.Width = side;
            _icon.Height = side;
        }
    }
}
