using System.Text;
using HaPlayer.Library;
using HaPlayer.Ui;

namespace HaPlayer.ViewModels;

/// <summary>Плейлисты в файлах: сохранение в .m3u8, общий плейлист через файл, импорт из других плееров.</summary>
public sealed partial class MainViewModel
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    // ---------- .m3u8 ----------

    /// <summary>
    /// Текст .m3u8. С relativeTo пути пишутся относительно этой папки: плейлист работает,
    /// если папку с ним и музыку перенести вместе (на флешку, на другой компьютер).
    /// Файл на другом диске остаётся с полным путём, по-другому до него не добраться.
    /// </summary>
    private string BuildM3u(string name, IEnumerable<string> paths, string? relativeTo)
    {
        var sb = new StringBuilder("#EXTM3U\n");
        sb.Append("#PLAYLIST:").Append(name).Append('\n');
        foreach (var path in paths)
        {
            if (_byPath.TryGetValue(path, out var t))
                sb.Append("#EXTINF:").Append((int)Math.Round(t.Model.DurationSeconds)).Append(',')
                  .Append(t.Artist).Append(" - ").Append(t.Title).Append('\n');
            sb.Append(relativeTo != null ? Relative(relativeTo, path) : path).Append('\n');
        }
        return sb.ToString();
    }

    private static string Relative(string dir, string path)
    {
        try
        {
            return Path.GetRelativePath(dir, path);
        }
        catch
        {
            return path;
        }
    }

    public async Task ExportM3uAsync(CollectionItem item, bool relative = false)
    {
        if (Dialogs == null || item.Tracks.Count == 0) return;
        var file = await Dialogs.PickSaveFileAsync(item.Name, ".m3u8");
        if (file == null) return;
        try
        {
            var text = BuildM3u(item.Name, item.Tracks.Select(t => t.Path), relative ? Path.GetDirectoryName(file) : null);
            await File.WriteAllTextAsync(file, text, Utf8NoBom);
        }
        catch (Exception e)
        {
            await Dialogs.MessageAsync(L.T("Не получилось сохранить"), e.Message);
        }
    }

    // ---------- Общий плейлист через файл ----------
    // Плейлист связан с .m3u8, например в облачной или сетевой папке. Изменения в HaPlayer сразу пишутся в файл,
    // а изменения файла (с другого компьютера) HaPlayer подхватывает при проверке раз в 20 секунд.

    private Avalonia.Threading.DispatcherTimer? _linkTimer;
    private bool _linkSyncing;

    public string? LinkedFile(PlaylistItem playlist) => Settings.LinkedPlaylists.GetValueOrDefault(playlist.Key);

    private void StartLinkSync()
    {
        if (_linkTimer != null || Settings.LinkedPlaylists.Count == 0) return;
        _linkTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _linkTimer.Tick += async (_, _) => await SyncLinkedAsync();
        _linkTimer.Start();
        _ = SyncLinkedAsync();
    }

    /// <summary>Связать с новым файлом: плейлист записывается в него, дальше файл и плейлист меняются вместе.</summary>
    public async Task LinkToNewFileAsync(PlaylistItem playlist)
    {
        if (Dialogs == null) return;
        var file = await Dialogs.PickSaveFileAsync(playlist.Name, ".m3u8");
        if (file == null) return;
        Settings.LinkedPlaylists[playlist.Key] = file;
        Settings.LinkedStamps.Remove(playlist.Key);
        Settings.Save();
        await WriteLinkedAsync(playlist.Id);
        StartLinkSync();
    }

    /// <summary>Связать с файлом, который уже есть (его завели на другом компьютере): плейлист берёт содержимое файла.</summary>
    public async Task LinkToExistingFileAsync(PlaylistItem playlist)
    {
        if (Dialogs == null) return;
        var file = (await Dialogs.PickPlaylistFilesAsync())
            .FirstOrDefault(f => Path.GetExtension(f).ToLowerInvariant() is ".m3u" or ".m3u8");
        if (file == null) return;
        var ok = await Dialogs.ConfirmAsync(L.T("Связать с файлом?"),
            L.F("Содержимое плейлиста «{0}» заменится треками из файла. Дальше изменения пойдут в обе стороны.", playlist.Name),
            L.T("Связать"));
        if (!ok) return;
        Settings.LinkedPlaylists[playlist.Key] = file;
        Settings.LinkedStamps.Remove(playlist.Key);
        Settings.Save();
        await SyncLinkedAsync();
        StartLinkSync();
    }

    public void Unlink(PlaylistItem playlist)
    {
        Settings.LinkedPlaylists.Remove(playlist.Key);
        Settings.LinkedStamps.Remove(playlist.Key);
        Settings.Save();
    }

    /// <summary>Записывает связанный плейлист в его файл. Папка недоступна — запишем при следующем изменении.</summary>
    private async Task WriteLinkedAsync(long id)
    {
        var key = id.ToString();
        if (!Settings.LinkedPlaylists.TryGetValue(key, out var file)) return;
        if (_playlistData.FirstOrDefault(p => p.Id == id) is not { } data) return;
        var text = BuildM3u(data.Name, data.Paths, Path.GetDirectoryName(file));
        try
        {
            var stamp = await Task.Run(() =>
            {
                File.WriteAllText(file, text, Utf8NoBom);
                return File.GetLastWriteTimeUtc(file).Ticks;
            });
            Settings.LinkedStamps[key] = stamp;
            Settings.Save();
        }
        catch
        {
            // сетевая папка отключена или файл занят
        }
    }

    /// <summary>Перечитывает связанные файлы, которые изменились не нами.</summary>
    private async Task SyncLinkedAsync()
    {
        if (_linkSyncing || Settings.LinkedPlaylists.Count == 0) return;
        _linkSyncing = true;
        try
        {
            var links = Settings.LinkedPlaylists.ToList();
            var stamps = new Dictionary<string, long>(Settings.LinkedStamps);
            var changed = await Task.Run(() =>
            {
                var result = new List<(string Key, long Stamp, ImportedPlaylist List)>();
                foreach (var (key, file) in links)
                {
                    try
                    {
                        if (!File.Exists(file)) continue;
                        var stamp = File.GetLastWriteTimeUtc(file).Ticks;
                        if (stamps.TryGetValue(key, out var known) && known == stamp) continue;
                        if (PlaylistImporter.Read(file).FirstOrDefault() is { } list) result.Add((key, stamp, list));
                    }
                    catch
                    {
                        // недоступен сейчас: проверим в следующий раз
                    }
                }
                return result;
            });

            var any = false;
            foreach (var (key, stamp, list) in changed)
            {
                var data = _playlistData.FirstOrDefault(p => p.Id.ToString() == key);
                if (data == null)
                {
                    Settings.LinkedPlaylists.Remove(key);
                    continue;
                }
                // Пустой файл при непустом плейлисте почти наверняка недокачанная облачная копия: не стираем.
                if (list.Paths.Count == 0 && data.Paths.Count > 0) continue;

                var onDisk = await Task.Run(() => list.Paths.Where(p => !_byPath.ContainsKey(p) && File.Exists(p)).ToList());
                if (onDisk.Count > 0) await ImportAsync(onDisk, refreshPage: false);
                var (found, _) = MatchImported(list);
                Settings.LinkedStamps[key] = stamp;
                if (found.SequenceEqual(data.Paths, StringComparer.OrdinalIgnoreCase)) continue;
                data.Paths.Clear();
                data.Paths.AddRange(found);
                _db.SetPlaylistTracks(data.Id, data.Paths);
                any = true;
            }
            Settings.Save();
            if (any)
            {
                Rebuild();
                RefreshPage();
            }
        }
        finally
        {
            _linkSyncing = false;
        }
    }

    // ---------- Импорт ----------

    /// <summary>
    /// Находит треки плейлиста в медиатеке. Файл на старом месте берётся как есть. Переехавший ищется
    /// по имени файла, а при нескольких совпадениях или другом имени по «исполнитель - название» из плейлиста.
    /// Missing: что найти не удалось, в виде, понятном человеку.
    /// </summary>
    private (List<string> Found, List<string> Missing) MatchImported(ImportedPlaylist list)
    {
        Dictionary<string, List<TrackItem>>? byName = null;
        Dictionary<string, TrackItem>? byHint = null;
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        static string HintKey(string s) => s.Trim().ToLowerInvariant();

        for (var i = 0; i < list.Paths.Count; i++)
        {
            var path = list.Paths[i];
            var hint = list.Hints != null && i < list.Hints.Count ? list.Hints[i] : null;
            TrackItem? match = null;
            if (_byPath.TryGetValue(path, out var direct) && !direct.IsMissing) match = direct;
            else
            {
                byName ??= _library.Where(t => !t.IsMissing)
                    .GroupBy(t => Path.GetFileName(t.Path).ToLowerInvariant())
                    .ToDictionary(g => g.Key, g => g.ToList());
                byHint ??= _library.Where(t => !t.IsMissing)
                    .GroupBy(t => HintKey($"{t.Artist} - {t.Title}"))
                    .ToDictionary(g => g.Key, g => g.First());
                var sameName = byName.GetValueOrDefault(Path.GetFileName(path).ToLowerInvariant());
                if (sameName is { Count: 1 }) match = sameName[0];
                else if (hint != null)
                {
                    match = sameName?.FirstOrDefault(t => HintKey($"{t.Artist} - {t.Title}") == HintKey(hint))
                            ?? byHint.GetValueOrDefault(HintKey(hint));
                }
                // Медиатека знает этот файл, просто диск сейчас не подключён: оставляем, он вернётся.
                match ??= direct;
            }
            if (match == null)
            {
                missing.Add(hint ?? Path.GetFileName(path));
                continue;
            }
            if (seen.Add(match.Path)) found.Add(match.Path);
        }
        return (found, missing);
    }
}
