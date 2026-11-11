using Avalonia.Markup.Xaml;

namespace HaPlayer.Ui;

/// <summary>
/// Перевод интерфейса. Исходный текст пишется по-русски, английский берётся из словаря Strings.En.
/// Язык выбирается при запуске; смена языка в настройках перезапускает программу.
/// </summary>
public static class L
{
    public static string Language { get; private set; } = "ru";
    public static bool IsEnglish => Language == "en";

    public static System.Globalization.CultureInfo Culture { get; private set; } = new("ru-RU");

    public static void SetLanguage(string? lang)
    {
        Language = lang == "en" ? "en" : "ru";
        Culture = new System.Globalization.CultureInfo(IsEnglish ? "en-US" : "ru-RU");
    }

    /// <summary>Текст на выбранном языке. Если перевода нет, остаётся русский.</summary>
    public static string T(string ru) => IsEnglish && Strings.En.TryGetValue(ru, out var en) ? en : ru;

    /// <summary>Значения-заглушки из базы («Неизвестный исполнитель», «Без альбома») на языке интерфейса.</summary>
    public static string Data(string value) =>
        value is HaPlayer.Library.TagReader.UnknownArtist or HaPlayer.Library.TagReader.NoAlbum ? T(value) : value;

    /// <summary>Текст с подстановками: шаблон «Удалить {0}?» переводится целиком, потом подставляются значения.</summary>
    public static string F(string ru, params object?[] args) => string.Format(Culture, T(ru), args);

    /// <summary>Число с существительным: 1 трек / 2 трека / 5 треков, 1 track / 2 tracks.</summary>
    public static string Plural(int n, string one, string few, string many, string enOne, string enMany)
    {
        if (IsEnglish) return $"{n} {(n == 1 ? enOne : enMany)}";
        var mod10 = n % 10;
        var mod100 = n % 100;
        var word = mod10 == 1 && mod100 != 11 ? one
            : mod10 is >= 2 and <= 4 && (mod100 < 12 || mod100 > 14) ? few
            : many;
        return $"{n} {word}";
    }
}

/// <summary>Перевод значения в биндинге: названия пресетов эквалайзера и другие строки-ключи.</summary>
public sealed class TrConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly TrConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is string s ? L.T(s) : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value;
}

/// <summary>Перевод в разметке: Text="{ui:T 'Настройки'}".</summary>
public sealed class T(string text) : MarkupExtension
{
    public string Text { get; } = text;

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}
