namespace HaPlayer.Library;

/// <summary>
/// Читает теги через TagLib#. Файл открывается только на чтение, Save() не вызывается никогда:
/// плеер ничего не пишет в файлы пользователя.
/// </summary>
public static class TagReader
{
    public const string UnknownArtist = "Неизвестный исполнитель";

    public static Track Read(string path)
    {
        var dirName = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? "";
        var track = new Track
        {
            Path = path,
            Title = System.IO.Path.GetFileNameWithoutExtension(path),
            Artist = UnknownArtist,
            // Если тега альбома нет, альбомом считается папка. Для неразмеченных коллекций это обычно верно.
            Album = string.IsNullOrWhiteSpace(dirName) ? "Без альбома" : dirName,
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
        }
        catch
        {
            // Битые или нестандартные теги: оставляем имя файла и папку.
        }

        return track;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.Select(v => v?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));
}
