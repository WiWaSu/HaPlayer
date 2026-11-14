using System.Text;
using System.Text.RegularExpressions;

namespace HaPlayer.Library;

/// <summary>Строка текста песни. Time — когда она начинается; у несинхронного текста null.</summary>
public sealed record LyricsLine(TimeSpan? Time, string Text);

/// <summary>
/// Текст песни: из файла .lrc рядом с треком (то же имя) или из тега в самом файле.
/// Файлы только читаются. Синхронный текст (с метками [мм:сс.xx]) подсвечивается по ходу трека.
/// </summary>
public sealed partial class Lyrics
{
    public required List<LyricsLine> Lines { get; init; }
    public bool Synced => Lines.Count > 0 && Lines[0].Time != null;

    static Lyrics() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Lyrics? Load(string trackPath)
    {
        string? text = null;
        try
        {
            var lrc = Path.ChangeExtension(trackPath, ".lrc");
            if (File.Exists(lrc)) text = ReadText(lrc);
        }
        catch
        {
            // не читается .lrc: пробуем тег
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            try
            {
                using var file = TagLib.File.Create(trackPath, TagLib.ReadStyle.Average);
                text = file.Tag.Lyrics;
            }
            catch
            {
                return null;
            }
        }
        return string.IsNullOrWhiteSpace(text) ? null : Parse(text);
    }

    /// <summary>.lrc бывают в UTF-8 и в Windows-1251. Если UTF-8 не читается без ошибок, берём 1251.</summary>
    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]")]
    private static partial Regex TimeTag();

    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTag();

    [GeneratedRegex(@"^\[[a-z]+:.*\]$", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTag();

    public static Lyrics Parse(string text)
    {
        var timed = new List<LyricsLine>();
        var plain = new List<LyricsLine>();
        var offset = TimeSpan.Zero;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (OffsetTag().Match(line) is { Success: true } off)
            {
                offset = TimeSpan.FromMilliseconds(int.Parse(off.Groups[1].Value));
                continue;
            }
            var tags = TimeTag().Matches(line);
            if (tags.Count == 0)
            {
                if (!MetaTag().IsMatch(line)) plain.Add(new LyricsLine(null, line));
                continue;
            }
            var lyric = TimeTag().Replace(line, "").Trim();
            foreach (Match m in tags)
            {
                var min = int.Parse(m.Groups[1].Value);
                var sec = double.Parse(m.Groups[2].Value.Replace(':', '.'), System.Globalization.CultureInfo.InvariantCulture);
                // В LRC положительный offset значит «показывать раньше».
                var t = TimeSpan.FromSeconds(min * 60 + sec) - offset;
                timed.Add(new LyricsLine(t < TimeSpan.Zero ? TimeSpan.Zero : t, lyric));
            }
        }
        if (timed.Count > 0) return new Lyrics { Lines = timed.OrderBy(l => l.Time).ToList() };

        // Пустые строки в начале и конце убираем, внутри оставляем: это паузы между куплетами.
        while (plain.Count > 0 && plain[0].Text.Length == 0) plain.RemoveAt(0);
        while (plain.Count > 0 && plain[^1].Text.Length == 0) plain.RemoveAt(plain.Count - 1);
        return new Lyrics { Lines = plain };
    }

    /// <summary>Номер строки, которая звучит в момент position. -1 — до первой строки.</summary>
    public int IndexAt(TimeSpan position)
    {
        if (!Synced) return -1;
        var lo = 0;
        var hi = Lines.Count - 1;
        var found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (Lines[mid].Time <= position)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found;
    }
}
