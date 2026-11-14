using Avalonia;
using Avalonia.Media;

namespace HaPlayer.Ui;

/// <summary>Цветовая палитра: акцент (кнопки, текущий трек), его оттенок при наведении и второй цвет для мелких деталей.</summary>
public sealed record Palette(string Id, string Name, Color Accent, Color AccentHover, Color Highlight, Color Deep);

/// <summary>Фон приложения: цвет окна и цвет панелей.</summary>
public sealed record Surface(string Id, string Name, Color Background, Color Panel, bool IsLight = false);

/// <summary>
/// Меняет цвета интерфейса на лету. Разметка берёт акцентные цвета через DynamicResource,
/// поэтому достаточно подменить ресурсы приложения.
/// </summary>
public static class ThemeService
{
    public static readonly IReadOnlyList<Palette> Palettes =
    [
        new("haplayer", L.T("Неон HaPlayer"), C("#FF2BD6"), C("#FF5CE1"), C("#E6FF00"), C("#4B1FA8")),
        new("acid", L.T("Кислотный"), C("#E6FF00"), C("#F0FF66"), C("#FF2BD6"), C("#3D5200")),
        new("mint", L.T("Мята"), C("#2BE3A0"), C("#62F0BE"), C("#FFD84D"), C("#0B5A45")),
        new("ocean", L.T("Океан"), C("#3DA9FF"), C("#70C0FF"), C("#FFD84D"), C("#123C8C")),
        new("violet", L.T("Фиалка"), C("#A07BFF"), C("#BBA0FF"), C("#E6FF00"), C("#3A1F8C")),
        new("sunset", L.T("Закат"), C("#FF7A3D"), C("#FF9C6B"), C("#FFE14D"), C("#8C2A12")),
        new("ruby", L.T("Рубин"), C("#FF3B5C"), C("#FF6B85"), C("#FFD84D"), C("#7A1028")),
        new("mono", L.T("Монохром"), C("#FFFFFF"), C("#E6E6E6"), C("#FFFFFF"), C("#3A3A3A")),
    ];

    public static readonly IReadOnlyList<Surface> Surfaces =
    [
        new("dark", L.T("Тёмная"), C("#000000"), C("#121212")),
        new("graphite", L.T("Графит"), C("#0E0E12"), C("#1B1B21")),
        new("light", L.T("Светлая"), C("#E9E9EC"), C("#FFFFFF"), true),
    ];

    public static bool IsLight => CurrentSurface.IsLight;

    /// <summary>Заглушка обложки и нота на ней. Один объект на всё приложение, цвет меняется вместе с темой.</summary>
    public static readonly SolidColorBrush CoverPlaceholder = new(C("#282828"));
    public static readonly SolidColorBrush CoverNote = new(C("#7A7A7A"));

    /// <summary>Кисть темы по ключу, для окон и элементов, собранных в коде.</summary>
    public static IBrush Brush(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var v) == true && v is IBrush b ? b : Brushes.Gray;

    public static Palette Current { get; private set; } = Palettes[0];
    public static Surface CurrentSurface { get; private set; } = Surfaces[0];

    public static event Action? Changed;

    public static readonly IReadOnlyList<string> Fonts = ["Inter", "Segoe UI", "Bahnschrift"];

    /// <summary>Множитель скруглений из настроек: 1, 0.5 или 0. Круглые кнопки и фото исполнителей не затрагивает.</summary>
    public static double RadiusFactor { get; set; } = 1;

    public static CornerRadius Radius(double value) => new(value * RadiusFactor);

    private static void ApplyRadii(Avalonia.Controls.IResourceDictionary r)
    {
        foreach (var v in new[] { 3, 4, 5, 6, 8, 10, 12 }) r["R" + v] = Radius(v);
    }

    private static Avalonia.Styling.Style? _fontStyle;

    /// <summary>Шрифт интерфейса для всех окон, меню и подсказок.</summary>
    public static void ApplyFont(string? name)
    {
        if (Application.Current is not { } app) return;
        if (_fontStyle != null) app.Styles.Remove(_fontStyle);
        _fontStyle = null;
        if (string.IsNullOrEmpty(name) || name == "Inter") return; // Inter задан в Program.cs как шрифт по умолчанию
        _fontStyle = new Avalonia.Styling.Style(x => Avalonia.Styling.Selectors.Is<Avalonia.Controls.TopLevel>(x))
        {
            Setters = { new Avalonia.Styling.Setter(Avalonia.Controls.Primitives.TemplatedControl.FontFamilyProperty, new FontFamily(name)) },
        };
        app.Styles.Add(_fontStyle);
    }

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
        // На белом фоне светлые акценты (белый, кислотный) не видны: затемняем их.
        if (IsLight)
            Current = Current with
            {
                Accent = Readable(Current.Accent),
                AccentHover = Readable(Current.AccentHover),
                Highlight = Readable(Current.Highlight),
            };
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = IsLight ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;

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
        ApplySurfaceColors(r, s.IsLight, p.Accent);
        ApplyRadii(r);

        Changed?.Invoke();
    }

    /// <summary>
    /// Нейтральные цвета: текст, карточки, наведение, меню, поля ввода, полупрозрачные подложки.
    /// В тёмной теме подложки белые с прозрачностью, в светлой чёрные.
    /// </summary>
    private static void ApplySurfaceColors(Avalonia.Controls.IResourceDictionary r, bool light, Color accent)
    {
        void B(string key, string dark, string lightHex) => r[key] = new SolidColorBrush(C(light ? lightHex : dark));

        B("TextBrush", "#FFFFFF", "#141414");
        B("SubBrush", "#B3B3B3", "#5C5C63");
        B("MutedBrush", "#6A6A6A", "#8E8E95");
        B("SoftTextBrush", "#E6E6E6", "#2A2A2E");
        B("PlaceholderBrush", "#8A8A8A", "#85858C");
        B("ElevatedBrush", "#1F1F1F", "#F1F1F4");
        B("HoverBrush", "#2A2A2A", "#E7E7EB");
        B("RaisedBrush", "#333333", "#DDDDE2");
        B("Raised2Brush", "#3E3E3E", "#D2D2D8");
        B("InputBrush", "#242424", "#EFEFF2");
        B("MenuBrush", "#282828", "#FFFFFF");
        B("LineBrush", "#4D4D4D", "#C4C4CA");
        B("InverseBrush", "#FFFFFF", "#141414");
        B("InverseTextBrush", "#000000", "#FFFFFF");
        B("InverseHoverBrush", "#F0F0F0", "#2A2A2E");
        B("InversePressedBrush", "#DDDDDD", "#000000");

        var tint = light ? Colors.Black : Colors.White;
        foreach (var a in new byte[] { 0x0D, 0x14, 0x1A, 0x1F, 0x24, 0x26, 0x2E, 0x33, 0x40, 0x66, 0x99, 0xB3 })
            r["Ov" + a.ToString("X2")] = new SolidColorBrush(WithAlpha(tint, light ? (byte)(a * 0.7) : a));
        r["DividerBrush"] = r["Ov1A"];

        // Стандартные элементы Fluent, у которых цвета заданы ключами
        void Alias(string key, string from) => r[key] = r[from];
        foreach (var k in new[] { "SliderTrackFill", "SliderTrackFillPointerOver", "SliderTrackFillPressed" }) Alias(k, "LineBrush");
        foreach (var k in new[] { "SliderTrackValueFill", "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed" }) Alias(k, "TextBrush");

        Alias("MenuFlyoutPresenterBackground", "MenuBrush");
        Alias("MenuFlyoutPresenterBorderBrush", "Ov14");
        foreach (var k in new[] { "MenuFlyoutItemBackgroundPointerOver", "MenuFlyoutItemBackgroundPressed",
                     "MenuFlyoutSubItemBackgroundPointerOver", "MenuFlyoutSubItemBackgroundSubMenuOpened" }) Alias(k, "Raised2Brush");
        Alias("MenuFlyoutItemForeground", "SoftTextBrush");
        Alias("MenuFlyoutItemForegroundPointerOver", "TextBrush");
        Alias("MenuFlyoutSubItemForeground", "SoftTextBrush");
        Alias("MenuFlyoutSubItemForegroundPointerOver", "TextBrush");
        Alias("MenuFlyoutSeparatorBackground", "Ov1A");

        Alias("ToolTipBackground", "MenuBrush");
        Alias("ToolTipForeground", "TextBrush");

        Alias("ComboBoxBackground", "HoverBrush");
        Alias("ComboBoxBackgroundPointerOver", "RaisedBrush");
        Alias("ComboBoxBackgroundPressed", "RaisedBrush");
        Alias("ComboBoxBackgroundFocused", "HoverBrush");
        Alias("ComboBoxBorderBrushPointerOver", "LineBrush");
        Alias("ComboBoxBorderBrushPressed", "LineBrush");
        Alias("ComboBoxDropDownBackground", "MenuBrush");
        Alias("ComboBoxDropDownBorderBrush", "Ov14");
        Alias("ComboBoxItemBackgroundPointerOver", "Raised2Brush");
        Alias("ComboBoxItemBackgroundSelected", "RaisedBrush");
        Alias("ComboBoxItemBackgroundSelectedPointerOver", "Raised2Brush");
        Alias("ComboBoxForeground", "TextBrush");
        Alias("ComboBoxItemForeground", "TextBrush");

        Alias("ScrollBarThumbFill", "Ov66");
        Alias("ScrollBarThumbFillPointerOver", "Ov99");
        Alias("ScrollBarThumbFillPressed", "OvB3");

        Alias("TextControlBackground", "InputBrush");
        Alias("TextControlBackgroundPointerOver", "HoverBrush");
        Alias("TextControlBackgroundFocused", "HoverBrush");
        Alias("TextControlBorderBrushPointerOver", "LineBrush");
        Alias("TextControlBorderBrushFocused", "TextBrush");
        foreach (var k in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" }) Alias(k, "TextBrush");
        foreach (var k in new[] { "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundFocused",
                     "TextControlPlaceholderForegroundPointerOver" }) Alias(k, "PlaceholderBrush");
        r["TextControlSelectionHighlightColor"] = new SolidColorBrush(accent);

        CoverPlaceholder.Color = C(light ? "#E2E2E7" : "#282828");
        CoverNote.Color = C(light ? "#9A9AA2" : "#7A7A7A");
    }

    /// <summary>Затемняет слишком светлый цвет, чтобы он читался на белом.</summary>
    private static Color Readable(Color c)
    {
        var lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
        return lum > 0.6 ? Mix(c, Colors.Black, Math.Min(0.6, (lum - 0.45) * 1.4)) : c;
    }

    /// <summary>Градиент обложки «Любимых треков» и «Всех треков».</summary>
    public static IBrush LikedBrush() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Current.Accent, 0), new GradientStop(Current.Deep, 1) },
    };

    /// <summary>Приглушённый акцент для шапок страниц без обложки.</summary>
    public static Color HeaderTint => IsLight ? Mix(Current.Accent, Colors.White, 0.7) : Mix(Current.Accent, Colors.Black, 0.6);

    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color C(string hex) => Color.Parse(hex);
}
