using System.Globalization;
using HaPlayer.Library;
using HaPlayer.Ui;

namespace HaPlayer.ViewModels;

/// <summary>Что проверяет условие умного плейлиста: текст, число или да/нет.</summary>
public enum SmartFieldKind
{
    Text,
    Number,
    Flag,
}

public sealed record SmartField(string Key, string Label, SmartFieldKind Kind);

public sealed record SmartOp(string Key, string Label);

/// <summary>
/// Условия умных плейлистов и их проверка. Всё считается по базе HaPlayer:
/// теги, оценки, лайки и история прослушиваний, файлы не читаются.
/// </summary>
public static class SmartFilter
{
    public static IReadOnlyList<SmartField> Fields =>
    [
        new("title", L.T("Название"), SmartFieldKind.Text),
        new("artist", L.T("Исполнитель"), SmartFieldKind.Text),
        new("album", L.T("Альбом"), SmartFieldKind.Text),
        new("genre", L.T("Жанр"), SmartFieldKind.Text),
        new("format", L.T("Формат"), SmartFieldKind.Text),
        new("folder", L.T("Папка"), SmartFieldKind.Text),
        new("year", L.T("Год"), SmartFieldKind.Number),
        new("rating", L.T("Оценка"), SmartFieldKind.Number),
        new("plays", L.T("Прослушиваний"), SmartFieldKind.Number),
        new("added", L.T("Добавлен, дней назад"), SmartFieldKind.Number),
        new("lastplayed", L.T("Слушал, дней назад"), SmartFieldKind.Number),
        new("minutes", L.T("Длительность, минут"), SmartFieldKind.Number),
        new("liked", L.T("В любимых"), SmartFieldKind.Flag),
        new("lossless", L.T("Без потерь"), SmartFieldKind.Flag),
    ];

    public static IReadOnlyList<SmartOp> Ops(SmartFieldKind kind) => kind switch
    {
        SmartFieldKind.Text =>
        [
            new("contains", L.T("содержит")),
            new("notcontains", L.T("не содержит")),
            new("is", L.T("равно")),
            new("isnot", L.T("не равно")),
        ],
        SmartFieldKind.Number =>
        [
            new("ge", L.T("не меньше")),
            new("le", L.T("не больше")),
            new("eq", L.T("равно")),
        ],
        _ =>
        [
            new("yes", L.T("да")),
            new("no", L.T("нет")),
        ],
    };

    /// <summary>Условия одной строкой для шапки страницы: «Жанр содержит Rock и Год не меньше 2000».</summary>
    public static string Describe(SmartPlaylistDef def)
    {
        var parts = def.Rules.Where(IsComplete).Select(r =>
        {
            var field = Field(r.Field);
            var op = Ops(field.Kind).FirstOrDefault(o => o.Key == r.Op)?.Label ?? "";
            return field.Kind == SmartFieldKind.Flag
                ? $"{field.Label}: {op}"
                : $"{field.Label} {op} {r.Value.Trim()}";
        });
        return string.Join(def.MatchAll ? L.T(" и ") : L.T(" или "), parts);
    }

    public static SmartField Field(string key) => Fields.FirstOrDefault(f => f.Key == key) ?? Fields[0];

    /// <summary>Треки, подходящие под условия, в порядке «исполнитель, альбом, номер».</summary>
    public static List<TrackItem> Apply(SmartPlaylistDef def, IEnumerable<TrackItem> library)
    {
        var rules = def.Rules.Where(IsComplete).ToList();
        if (rules.Count == 0) return [];
        var now = DateTime.UtcNow;
        return library
            .Where(t => def.MatchAll ? rules.All(r => Match(r, t, now)) : rules.Any(r => Match(r, t, now)))
            .OrderBy(t => t.Artist, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.AlbumName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.Model.DiscNo)
            .ThenBy(t => t.Model.TrackNo)
            .ToList();
    }

    /// <summary>Условие заполнено: у текста и числа есть значение, у флага оно не нужно.</summary>
    public static bool IsComplete(SmartRule r) => Field(r.Field).Kind switch
    {
        SmartFieldKind.Text => !string.IsNullOrWhiteSpace(r.Value),
        SmartFieldKind.Number => TryNumber(r.Value, out _),
        _ => true,
    };

    private static bool TryNumber(string s, out double value) =>
        double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool Match(SmartRule r, TrackItem t, DateTime now)
    {
        var field = Field(r.Field);
        switch (field.Kind)
        {
            case SmartFieldKind.Flag:
            {
                var on = r.Field switch
                {
                    "liked" => t.IsLiked,
                    "lossless" => t.Model.IsLossless,
                    _ => false,
                };
                return r.Op == "no" ? !on : on;
            }
            case SmartFieldKind.Number:
            {
                if (!TryNumber(r.Value, out var v)) return false;
                double? x = r.Field switch
                {
                    "year" => t.Model.Year > 0 ? t.Model.Year : null,
                    "rating" => t.Rating,
                    "plays" => t.PlayCount,
                    "added" => t.Model.AddedTicks > 0 ? (now - new DateTime(t.Model.AddedTicks, DateTimeKind.Utc)).TotalDays : null,
                    // Ни разу не слушал — «давно»: для «не меньше N дней» подходит, для «не больше» нет.
                    "lastplayed" => t.LastPlayedTicks > 0 ? (now - new DateTime(t.LastPlayedTicks, DateTimeKind.Utc)).TotalDays : double.MaxValue,
                    "minutes" => t.Model.DurationSeconds / 60,
                    _ => null,
                };
                if (x is not { } n) return false;
                return r.Op switch
                {
                    "ge" => n >= v,
                    "le" => n <= v,
                    // Дни и минуты дробные: «равно» сравнивает с точностью до целого.
                    _ => Math.Floor(n) == Math.Floor(v),
                };
            }
            default:
            {
                var text = r.Field switch
                {
                    "title" => t.Title,
                    "artist" => t.Artist,
                    "album" => t.AlbumName,
                    "genre" => t.Model.Genre,
                    "format" => t.Extension,
                    "folder" => System.IO.Path.GetDirectoryName(t.Path) ?? "",
                    _ => "",
                };
                var v = r.Value.Trim();
                var cmp = StringComparison.CurrentCultureIgnoreCase;
                // Жанров в теге бывает несколько через «;» или «/»: «равно» проверяет каждый.
                var parts = r.Field == "genre"
                    ? text.Split([';', '/', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    : [text.Trim()];
                return r.Op switch
                {
                    "notcontains" => !text.Contains(v, cmp),
                    "is" => parts.Any(p => string.Equals(p, v, cmp)),
                    "isnot" => !parts.Any(p => string.Equals(p, v, cmp)),
                    _ => text.Contains(v, cmp),
                };
            }
        }
    }
}
