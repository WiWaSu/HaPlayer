using HaPlayer.Ui;
using System.Text;
using System.Xml.Linq;

namespace HaPlayer.Library;

public sealed record ImportedPlaylist(string Name, List<string> Paths);

/// <summary>
/// Читает плейлисты других плееров. Файлы плейлистов только читаются.
/// Форматы: M3U/M3U8 (Winamp, VLC, foobar2000, AIMP), PLS, XSPF, WPL/ZPL (Windows Media Player),
/// AIMPPL4 (AIMP), iTunes Library.xml. Двоичный .fpl foobar2000 не читается: его нужно сохранить как M3U8.
/// </summary>
public static class PlaylistImporter
{
    public static readonly string[] Extensions = [".m3u", ".m3u8", ".pls", ".xspf", ".wpl", ".zpl", ".aimppl4", ".xml"];

    static PlaylistImporter()
    {
        // Нужна кодировка Windows-1251: старые M3U из Winamp часто сохранены в ней.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Плейлисты из медиатеки Winamp и его текущий плейлист, если Winamp установлен.</summary>
    public static List<ImportedPlaylist> FindWinampPlaylists()
    {
        var result = new List<ImportedPlaylist>();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Winamp");
        if (!Directory.Exists(root)) return result;

        // Индекс медиатеки: Plugins\ml\playlists\playlists.xml (в старых версиях лежал уровнем выше).
        var mlDir = Path.Combine(root, "Plugins", "ml", "playlists");
        foreach (var index in new[] { Path.Combine(mlDir, "playlists.xml"), Path.Combine(root, "Plugins", "ml", "playlists.xml") })
        {
            if (!File.Exists(index)) continue;
            try
            {
                foreach (var el in LoadXmlLenient(index).Descendants("playlist"))
                {
                    var file = (string?)el.Attribute("filename");
                    var title = (string?)el.Attribute("title");
                    if (string.IsNullOrEmpty(file)) continue;
                    var full = Path.IsPathRooted(file) ? file : Path.Combine(mlDir, file);
                    if (!File.Exists(full)) continue;
                    var paths = ReadM3U(full);
                    if (paths.Count > 0) result.Add(new ImportedPlaylist(title ?? Path.GetFileNameWithoutExtension(file), paths));
                }
            }
            catch
            {
                // испорченный playlists.xml: просто ничего не нашли
            }
            break;
        }

        foreach (var name in new[] { "Winamp.m3u8", "Winamp.m3u" })
        {
            var current = Path.Combine(root, name);
            if (!File.Exists(current)) continue;
            var paths = ReadM3U(current);
            if (paths.Count > 0) result.Add(new ImportedPlaylist(L.T("Winamp: текущий плейлист"), paths));
            break;
        }
        return result;
    }

    /// <summary>Читает один файл. iTunes Library.xml даёт сразу несколько плейлистов.</summary>
    public static List<ImportedPlaylist> Read(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        try
        {
            return Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".m3u" or ".m3u8" => [new(name, ReadM3U(file))],
                ".pls" => [new(name, ReadPls(file))],
                ".xspf" => [new(ReadXspfTitle(file) ?? name, ReadXspf(file))],
                ".wpl" or ".zpl" => [new(ReadWplTitle(file) ?? name, ReadWpl(file))],
                ".aimppl4" => [new(name, ReadAimp(file))],
                ".xml" => ReadITunes(file),
                _ => [],
            };
        }
        catch
        {
            return [];
        }
    }

    // ---------- Форматы ----------

    private static List<string> ReadM3U(string file)
    {
        var baseDir = Path.GetDirectoryName(file)!;
        return ReadLines(file)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => Resolve(l, baseDir))
            .OfType<string>()
            .ToList();
    }

    private static List<string> ReadPls(string file)
    {
        var baseDir = Path.GetDirectoryName(file)!;
        return ReadLines(file)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("File", StringComparison.OrdinalIgnoreCase) && l.Contains('='))
            .Select(l => Resolve(l[(l.IndexOf('=') + 1)..], baseDir))
            .OfType<string>()
            .ToList();
    }

    private static List<string> ReadXspf(string file)
    {
        var baseDir = Path.GetDirectoryName(file)!;
        return XDocument.Load(file).Descendants()
            .Where(e => e.Name.LocalName == "location")
            .Select(e => Resolve(e.Value.Trim(), baseDir))
            .OfType<string>()
            .ToList();
    }

    private static string? ReadXspfTitle(string file) =>
        XDocument.Load(file).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value.Trim() is { Length: > 0 } t ? t : null;

    private static List<string> ReadWpl(string file)
    {
        var baseDir = Path.GetDirectoryName(file)!;
        return XDocument.Load(file).Descendants()
            .Where(e => e.Name.LocalName == "media")
            .Select(e => (string?)e.Attribute("src"))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => Resolve(s!, baseDir))
            .OfType<string>()
            .ToList();
    }

    private static string? ReadWplTitle(string file) =>
        XDocument.Load(file).Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value.Trim() is { Length: > 0 } t ? t : null;

    /// <summary>AIMP: после строки #-----CONTENT-----# идут «путь|название|…», строки групп начинаются с «-».</summary>
    private static List<string> ReadAimp(string file)
    {
        var baseDir = Path.GetDirectoryName(file)!;
        var result = new List<string>();
        var inContent = false;
        foreach (var raw in ReadLines(file))
        {
            var line = raw.Trim();
            if (line.StartsWith("#-----", StringComparison.Ordinal))
            {
                inContent = line.Contains("CONTENT", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inContent || line.Length == 0 || line.StartsWith('-')) continue;
            var path = line.Split('|')[0];
            if (Resolve(path, baseDir) is { } p) result.Add(p);
        }
        return result;
    }

    /// <summary>iTunes / Музыка: Library.xml в формате plist. Служебные списки (вся медиатека, подкасты) пропускаются.</summary>
    private static List<ImportedPlaylist> ReadITunes(string file)
    {
        var doc = XDocument.Load(file);
        var root = doc.Root?.Element("dict");
        if (root == null) return [];
        var top = ParseDict(root);

        var locations = new Dictionary<string, string>();
        if (top.GetValueOrDefault("Tracks") is XElement tracksDict)
        {
            foreach (var (id, value) in ParseDict(tracksDict))
            {
                if (value is not XElement trackDict) continue;
                var t = ParseDict(trackDict);
                if (t.GetValueOrDefault("Location") is XElement loc && Resolve(loc.Value, "") is { } path)
                    locations[id] = path;
            }
        }

        var result = new List<ImportedPlaylist>();
        if (top.GetValueOrDefault("Playlists") is XElement array)
        {
            foreach (var plDict in array.Elements("dict"))
            {
                var pl = ParseDict(plDict);
                if (pl.ContainsKey("Master") || pl.ContainsKey("Distinguished Kind") || pl.ContainsKey("Folder")) continue;
                var name = (pl.GetValueOrDefault("Name") as XElement)?.Value ?? "iTunes";
                var paths = new List<string>();
                if (pl.GetValueOrDefault("Playlist Items") is XElement items)
                {
                    foreach (var item in items.Elements("dict"))
                    {
                        var id = (ParseDict(item).GetValueOrDefault("Track ID") as XElement)?.Value;
                        if (id != null && locations.TryGetValue(id, out var p)) paths.Add(p);
                    }
                }
                if (paths.Count > 0) result.Add(new ImportedPlaylist(name, paths));
            }
        }
        return result;
    }

    /// <summary>plist dict: чередование &lt;key&gt; и значения.</summary>
    private static Dictionary<string, object> ParseDict(XElement dict)
    {
        var map = new Dictionary<string, object>();
        string? key = null;
        foreach (var el in dict.Elements())
        {
            if (el.Name == "key") key = el.Value;
            else if (key != null)
            {
                map[key] = el;
                key = null;
            }
        }
        return map;
    }

    // ---------- Общее ----------

    /// <summary>
    /// Winamp пишет в заголовке encoding="UTF-16", даже когда сам файл в другой кодировке,
    /// и XDocument.Load на этом падает. Читаем текст сами и убираем заголовок.
    /// </summary>
    private static XDocument LoadXmlLenient(string file)
    {
        var text = string.Join("\n", ReadLines(file));
        if (text.StartsWith("<?xml", StringComparison.Ordinal))
        {
            var end = text.IndexOf("?>", StringComparison.Ordinal);
            if (end > 0) text = text[(end + 2)..];
        }
        return XDocument.Parse(text);
    }

    /// <summary>Текст плейлиста: UTF-8 (с BOM или без), иначе Windows-1251.</summary>
    private static IEnumerable<string> ReadLines(string file)
    {
        var bytes = File.ReadAllBytes(file);
        string text;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        else
        {
            try
            {
                text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
                if (text.Length > 0 && text[0] == '﻿') text = text[1..];
            }
            catch (DecoderFallbackException)
            {
                text = Encoding.GetEncoding(1251).GetString(bytes);
            }
        }
        return text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
    }

    /// <summary>Приводит запись плейлиста к полному пути. Ссылки на интернет-радио и прочие URL пропускаются.</summary>
    private static string? Resolve(string entry, string baseDir)
    {
        entry = entry.Trim().Trim('"');
        if (entry.Length == 0) return null;

        if (entry.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                entry = new Uri(entry).LocalPath;
            }
            catch
            {
                return null;
            }
        }
        else if (entry.Contains("://")) return null;

        try
        {
            var full = Path.IsPathRooted(entry) ? entry : Path.GetFullPath(Path.Combine(baseDir, entry));
            return AudioFormats.IsAudio(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }
}
