namespace HaPlayer.Library;

/// <summary>
/// Читает теги через TagLib#. Файл открывается только на чтение, Save() не вызывается никогда:
/// плеер ничего не пишет в файлы пользователя.
/// </summary>
public static class TagReader
{
    // Эти два значения пишутся в базу как данные. На экране их переводит L.Data.
    public const string UnknownArtist = "Неизвестный исполнитель";
    public const string NoAlbum = "Без альбома";

    public static Track Read(string path)
    {
        var dirName = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? "";
        var track = new Track
        {
            Path = path,
            Title = System.IO.Path.GetFileNameWithoutExtension(path),
            Artist = UnknownArtist,
            // Если тега альбома нет, альбомом считается папка. Для неразмеченных коллекций это обычно верно.
            Album = string.IsNullOrWhiteSpace(dirName) ? NoAlbum : dirName,
        };

        try
        {
            track.ModifiedTicks = File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch
        {
            // файл мог исчезнуть между обходом папки и чтением
        }

        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            if (!string.IsNullOrWhiteSpace(tag.Title)) track.Title = tag.Title.Trim();

            var artist = FirstNonEmpty(tag.FirstPerformer, tag.FirstAlbumArtist);
            if (artist != null) track.Artist = artist;

            track.AlbumArtist = FirstNonEmpty(tag.FirstAlbumArtist);
            if (!string.IsNullOrWhiteSpace(tag.Album)) track.Album = tag.Album.Trim();
            track.TrackNo = (int)tag.Track;
            track.DiscNo = (int)tag.Disc;
            track.Year = (int)tag.Year;
            track.DurationSeconds = file.Properties?.Duration.TotalSeconds ?? 0;
            ReadTech(file, track);
        }
        catch
        {
            // Битые или нестандартные теги: оставляем имя файла и папку.
        }

        return track;
    }

    private static void ReadTech(TagLib.File file, Track track)
    {
        if (file.Properties is not { } p) return;
        track.Bitrate = p.AudioBitrate;
        track.SampleRate = p.AudioSampleRate;
        track.Channels = p.AudioChannels;
    }

    /// <summary>Только битрейт, частота и каналы: для треков, добавленных до того, как плеер их запоминал.</summary>
    public static bool ReadTechOnly(Track track)
    {
        try
        {
            using var file = TagLib.File.Create(track.Path, TagLib.ReadStyle.Average);
            ReadTech(file, track);
            return track.SampleRate > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.Select(v => v?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));
}
