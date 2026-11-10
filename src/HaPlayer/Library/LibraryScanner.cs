using System.Collections.Concurrent;

namespace HaPlayer.Library;

public static class LibraryScanner
{
    /// <summary>Собирает аудиофайлы из списка файлов и папок (папки обходятся рекурсивно).</summary>
    public static List<string> CollectFiles(IEnumerable<string> paths)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
        };

        var result = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
                result.AddRange(Directory.EnumerateFiles(p, "*", options).Where(AudioFormats.IsAudio));
            else if (File.Exists(p) && AudioFormats.IsAudio(p))
                result.Add(p);
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Читает теги новых и изменённых файлов. Файлы, у которых время изменения совпало с базой, пропускаются.
    /// </summary>
    public static List<Track> ReadChanged(
        IReadOnlyList<string> files,
        IReadOnlyDictionary<string, long> known,
        IProgress<(int Done, int Total)>? progress = null)
    {
        var todo = files.Where(f => !known.TryGetValue(f, out var ticks) || ticks != SafeTicks(f)).ToList();
        var results = new ConcurrentDictionary<int, Track>();
        var done = 0;

        Parallel.For(0, todo.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
            results[i] = TagReader.Read(todo[i]);
            var n = Interlocked.Increment(ref done);
            if (n % 25 == 0 || n == todo.Count) progress?.Report((n, todo.Count));
        });

        return Enumerable.Range(0, todo.Count).Select(i => results[i]).ToList();
    }

    private static long SafeTicks(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch
        {
            return -1;
        }
    }
}
