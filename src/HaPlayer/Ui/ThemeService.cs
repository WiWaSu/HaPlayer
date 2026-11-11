using Avalonia;
using Avalonia.Media;

namespace HaPlayer.Ui;

/// <summary>Цветовая палитра: акцент (кнопки, текущий трек), его оттенок при наведении и второй цвет для мелких деталей.</summary>
public sealed record Palette(string Id, string Name, Color Accent, Color AccentHover, Color Highlight, Color Deep);

/// <summary>Фон приложения: цвет окна и цвет панелей.</summary>
public sealed record Surface(string Id, string Name, Color Background, Color Panel);

/// <summary>
/// Меняет цвета интерфейса на лету. Разметка берёт акцентные цвета через DynamicResource,
/// поэтому достаточно подменить ресурсы приложения.
/// </summary>
public static class ThemeService
{
    public static readonly IReadOnlyList<Palette> Palettes =
    [
        new("haplayer", "Неон HaPlayer", C("#FF2BD6"), C("#FF5CE1"), C("#E6FF00"), C("#4B1FA8")),
        new("acid", "Кислотный", C("#E6FF00"), C("#F0FF66"), C("#FF2BD6"), C("#3D5200")),
        new("mint", "Мята", C("#2BE3A0"), C("#62F0BE"), C("#FFD84D"), C("#0B5A45")),
        new("ocean", "Океан", C("#3DA9FF"), C("#70C0FF"), C("#FFD84D"), C("#123C8C")),
        new("violet", "Фиалка", C("#A07BFF"), C("#BBA0FF"), C("#E6FF00"), C("#3A1F8C")),
        new("sunset", "Закат", C("#FF7A3D"), C("#FF9C6B"), C("#FFE14D"), C("#8C2A12")),
        new("ruby", "Рубин", C("#FF3B5C"), C("#FF6B85"), C("#FFD84D"), C("#7A1028")),
        new("mono", "Монохром", C("#FFFFFF"), C("#E6E6E6"), C("#FFFFFF"), C("#3A3A3A")),
    ];

    public static readonly IReadOnlyList<Surface> Surfaces =
    [
        new("dark", "Тёмная", C("#000000"), C("#121212")),
        new("graphite", "Графит", C("#0E0E12"), C("#1B1B21")),
    ];

    public static Palette Current { get; private set; } = Palettes[0];
    public static Surface CurrentSurface { get; private set; } = Surfaces[0];

    public static event Action? Changed;

    /// <summary>Разбирает «#RRGGBB» или «RRGGBB». null — строка не похожа на цвет.</summary>
    public static Color? ParseHex(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        if (!s.StartsWith('#')) s = "#" + s;
        return s.Length == 7 && Color.TryParse(s, out var c) ? c : null;
    }

    public static void Apply(string paletteId, string surfaceId, string? customAccent = null)
    {
        Current = Palettes.FirstOrDefault(p => p.Id == paletteId) ?? Palettes[0];
        // Свой цвет заменяет акцент палитры. Оттенок при наведении светлее, «глубокий» для градиентов темнее.
        if (ParseHex(customAccent) is { } accent)
            Current = Current with
            {
                Id = "custom",
                Accent = accent,
                AccentHover = Mix(accent, Colors.White, 0.25),
                Deep = Mix(accent, Colors.Black, 0.65),
            };
        CurrentSurface = Surfaces.FirstOrDefault(s => s.Id == surfaceId) ?? Surfaces[0];
        if (Application.Current is not { } app) return;

        var r = app.Resources;
        var p = Current;
        r["AccentColor"] = p.Accent;
        r["AccentBrush"] = new SolidColorBrush(p.Accent);
        r["AccentHoverBrush"] = new SolidColorBrush(p.AccentHover);
        r["HighlightBrush"] = new SolidColorBrush(p.Highlight);
        r["LikedBrush"] = LikedBrush();
        r["LibraryArtBrush"] = LikedBrush();
        r["GlowBrush"] = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.3, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.3, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.6, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.6, RelativeUnit.Relative),
            GradientStops = { new GradientStop(WithAlpha(p.Accent, 0x26), 0), new GradientStop(WithAlpha(p.Accent, 0), 1) },
        };

        // Ползунки Fluent
        r["SliderTrackValueFillPointerOver"] = new SolidColorBrush(p.Accent);
        r["SliderTrackValueFillPressed"] = new SolidColorBrush(p.Accent);

        // Стандартные элементы Fluent (переключатели, выделение в списках, фокус)
        r["SystemAccentColor"] = p.Accent;
        r["SystemAccentColorLight1"] = p.AccentHover;
        r["SystemAccentColorLight2"] = p.AccentHover;
        r["SystemAccentColorLight3"] = p.AccentHover;
        r["SystemAccentColorDark1"] = p.Accent;
        r["SystemAccentColorDark2"] = p.Deep;
        r["SystemAccentColorDark3"] = p.Deep;

        var s = CurrentSurface;
        r["BgBrush"] = new SolidColorBrush(s.Background);
        r["PanelBrush"] = new SolidColorBrush(s.Panel);

        Changed?.Invoke();
    }

    /// <summary>Градиент обложки «Любимых треков» и «Всех треков».</summary>
    public static IBrush LikedBrush() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Current.Accent, 0), new GradientStop(Current.Deep, 1) },
    };

    /// <summary>Приглушённый акцент для шапок страниц без обложки.</summary>
    public static Color HeaderTint => Mix(Current.Accent, Colors.Black, 0.6);

    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color C(string hex) => Color.Parse(hex);
}
