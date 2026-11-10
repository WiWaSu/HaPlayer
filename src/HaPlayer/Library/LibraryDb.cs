using Microsoft.Data.Sqlite;

namespace HaPlayer.Library;

public sealed record PlaylistData(long Id, string Name, List<string> Paths);

/// <summary>
/// Медиатека в одном файле SQLite (%APPDATA%\HaPlayer\library.db).
/// Формат открытый: файл можно открыть любым просмотрщиком SQLite.
/// </summary>
public sealed class LibraryDb : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly Lock _lock = new();

    public LibraryDb(string file)
    {
        _conn = new SqliteConnection($"Data Source={file}");
        _conn.Open();
        Exec("""
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS tracks (
                path         TEXT PRIMARY KEY,
                title        TEXT NOT NULL,
                artist       TEXT NOT NULL,
                album        TEXT NOT NULL,
                album_artist TEXT,
                track_no     INTEGER NOT NULL DEFAULT 0,
                disc_no      INTEGER NOT NULL DEFAULT 0,
                year         INTEGER NOT NULL DEFAULT 0,
                duration     REAL    NOT NULL DEFAULT 0,
                mtime        INTEGER NOT NULL DEFAULT 0,
                added        INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS playlists (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                name    TEXT NOT NULL,
                created INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS playlist_tracks (
                playlist_id INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
                position    INTEGER NOT NULL,
                path        TEXT NOT NULL,
                PRIMARY KEY (playlist_id, position)
            );
            -- Треки, которые пользователь убрал из медиатеки (удалил альбом или исполнителя).
            -- Файлы на диске целы; проверка папок их не возвращает, явное добавление возвращает.
            CREATE TABLE IF NOT EXISTS hidden (
                path TEXT PRIMARY KEY
            );
            -- История прослушивания. Хранится только здесь, никуда не отправляется.
            CREATE TABLE IF NOT EXISTS history (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                path      TEXT NOT NULL,
                played_at INTEGER NOT NULL
            );
            -- «Любимые треки»: сердечко у трека.
            CREATE TABLE IF NOT EXISTS likes (
                path  TEXT PRIMARY KEY,
                added INTEGER NOT NULL
            );
            -- Фото, которые пользователь поставил сам. kind: playlist / album / artist.
            CREATE TABLE IF NOT EXISTS images (
                kind TEXT NOT NULL,
                key  TEXT NOT NULL,
                file TEXT NOT NULL,
                PRIMARY KEY (kind, key)
            );
            """);

        // Колонки, появившиеся позже первой версии базы.
        AddColumnIfMissing("tracks", "user_album_key", "TEXT");
        AddColumnIfMissing("tracks", "user_artist", "TEXT");
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void AddColumnIfMissing(string table, string column, string type)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        if (Convert.ToInt64(cmd.ExecuteScalar()) == 0) Exec($"ALTER TABLE {table} ADD COLUMN {column} {type}");
    }

    // ---------- Треки ----------

    /// <summary>Все треки в порядке добавления.</summary>
    public List<Track> LoadAll()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT path, title, artist, album, album_artist, track_no, disc_no, year, duration, mtime,
                       user_album_key, user_artist, added
                FROM tracks ORDER BY added, rowid
                """;
            using var r = cmd.ExecuteReader();
            var list = new List<Track>();
            while (r.Read())
            {
                list.Add(new Track
                {
                    Path = r.GetString(0),
                    Title = r.GetString(1),
                    Artist = r.GetString(2),
                    Album = r.GetString(3),
                    AlbumArtist = r.IsDBNull(4) ? null : r.GetString(4),
                    TrackNo = r.GetInt32(5),
                    DiscNo = r.GetInt32(6),
                    Year = r.GetInt32(7),
                    DurationSeconds = r.GetDouble(8),
                    ModifiedTicks = r.GetInt64(9),
                    UserAlbumKey = r.IsDBNull(10) ? null : r.GetString(10),
                    UserArtist = r.IsDBNull(11) ? null : r.GetString(11),
                    AddedTicks = r.GetInt64(12),
                });
            }
            return list;
        }
    }

    /// <summary>Добавляет или обновляет треки по тегам. Привязку пользователя (user_*) не трогает.</summary>
    public void Upsert(IReadOnlyCollection<Track> tracks)
    {
        if (tracks.Count == 0) return;
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO tracks (path, title, artist, album, album_artist, track_no, disc_no, year, duration, mtime, added)
                VALUES ($path, $title, $artist, $album, $albumArtist, $trackNo, $discNo, $year, $duration, $mtime, $added)
                ON CONFLICT(path) DO UPDATE SET
                    title = excluded.title, artist = excluded.artist, album = excluded.album,
                    album_artist = excluded.album_artist, track_no = excluded.track_no, disc_no = excluded.disc_no,
                    year = excluded.year, duration = excluded.duration, mtime = excluded.mtime
                """;
            var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
            var pTitle = cmd.Parameters.Add("$title", SqliteType.Text);
            var pArtist = cmd.Parameters.Add("$artist", SqliteType.Text);
            var pAlbum = cmd.Parameters.Add("$album", SqliteType.Text);
            var pAlbumArtist = cmd.Parameters.Add("$albumArtist", SqliteType.Text);
            var pTrackNo = cmd.Parameters.Add("$trackNo", SqliteType.Integer);
            var pDiscNo = cmd.Parameters.Add("$discNo", SqliteType.Integer);
            var pYear = cmd.Parameters.Add("$year", SqliteType.Integer);
            var pDuration = cmd.Parameters.Add("$duration", SqliteType.Real);
            var pMtime = cmd.Parameters.Add("$mtime", SqliteType.Integer);
            var pAdded = cmd.Parameters.Add("$added", SqliteType.Integer);

            var now = DateTime.UtcNow.Ticks;
            foreach (var t in tracks)
            {
                pPath.Value = t.Path;
                pTitle.Value = t.Title;
                pArtist.Value = t.Artist;
                pAlbum.Value = t.Album;
                pAlbumArtist.Value = (object?)t.AlbumArtist ?? DBNull.Value;
                pTrackNo.Value = t.TrackNo;
                pDiscNo.Value = t.DiscNo;
                pYear.Value = t.Year;
                pDuration.Value = t.DurationSeconds;
                pMtime.Value = t.ModifiedTicks;
                pAdded.Value = now++;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Убирает из медиатеки треки из папки. Сами файлы не трогаются.</summary>
    public void RemoveUnder(string folder)
    {
        var prefix = folder.TrimEnd('\\', '/') + "\\";
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM tracks WHERE substr(lower(path), 1, length($p)) = lower($p)";
            cmd.Parameters.AddWithValue("$p", prefix);
            cmd.ExecuteNonQuery();
        }
    }

    public void SetUserAlbum(IEnumerable<string> paths, string? albumKey) =>
        UpdateColumn("user_album_key", paths, albumKey);

    public void SetUserArtist(IEnumerable<string> paths, string? artist) =>
        UpdateColumn("user_artist", paths, artist);

    private void UpdateColumn(string column, IEnumerable<string> paths, string? value)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"UPDATE tracks SET {column} = $v WHERE path = $p";
            var pv = cmd.Parameters.Add("$v", SqliteType.Text);
            var pp = cmd.Parameters.Add("$p", SqliteType.Text);
            pv.Value = (object?)value ?? DBNull.Value;
            foreach (var p in paths)
            {
                pp.Value = p;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // ---------- Плейлисты ----------

    public List<PlaylistData> LoadPlaylists()
    {
        lock (_lock)
        {
            var map = new Dictionary<long, PlaylistData>();
            var order = new List<PlaylistData>();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, name FROM playlists ORDER BY created, id";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var p = new PlaylistData(r.GetInt64(0), r.GetString(1), []);
                    map[p.Id] = p;
                    order.Add(p);
                }
            }
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT playlist_id, path FROM playlist_tracks ORDER BY playlist_id, position";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    if (map.TryGetValue(r.GetInt64(0), out var p)) p.Paths.Add(r.GetString(1));
            }
            return order;
        }
    }

    public long CreatePlaylist(string name)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO playlists (name, created) VALUES ($n, $c); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.Ticks);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    public void RenamePlaylist(long id, string name)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE playlists SET name = $n WHERE id = $id";
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeletePlaylist(long id)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM playlist_tracks WHERE playlist_id = $id;
                DELETE FROM playlists WHERE id = $id;
                DELETE FROM images WHERE kind = 'playlist' AND key = $key;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$key", id.ToString());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Перезаписывает содержимое плейлиста целиком: так проще держать позиции подряд.</summary>
    public void SetPlaylistTracks(long id, IReadOnlyList<string> paths)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            using (var del = _conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM playlist_tracks WHERE playlist_id = $id";
                del.Parameters.AddWithValue("$id", id);
                del.ExecuteNonQuery();
            }
            using (var ins = _conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO playlist_tracks (playlist_id, position, path) VALUES ($id, $pos, $path)";
                var pId = ins.Parameters.Add("$id", SqliteType.Integer);
                var pPos = ins.Parameters.Add("$pos", SqliteType.Integer);
                var pPath = ins.Parameters.Add("$path", SqliteType.Text);
                pId.Value = id;
                for (var i = 0; i < paths.Count; i++)
                {
                    pPos.Value = i;
                    pPath.Value = paths[i];
                    ins.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
    }

    // ---------- Скрытые треки ----------

    public HashSet<string> LoadHidden()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT path FROM hidden";
            using var r = cmd.ExecuteReader();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (r.Read()) set.Add(r.GetString(0));
            return set;
        }
    }

    /// <summary>Убирает треки из медиатеки и запоминает их как скрытые. Файлы не трогаются.</summary>
    public void Hide(IReadOnlyCollection<string> paths)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            using var del = _conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM tracks WHERE path = $p; INSERT OR IGNORE INTO hidden (path) VALUES ($p);";
            var p = del.Parameters.Add("$p", SqliteType.Text);
            foreach (var path in paths)
            {
                p.Value = path;
                del.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void Unhide(IReadOnlyCollection<string> paths)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM hidden WHERE path = $p";
            var p = cmd.Parameters.Add("$p", SqliteType.Text);
            foreach (var path in paths)
            {
                p.Value = path;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // ---------- История прослушивания ----------

    public void AddHistory(string path)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO history (path, played_at) VALUES ($p, $t)";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.Ticks);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Последние прослушивания, новые сверху.</summary>
    public List<(string Path, long PlayedAt)> LoadHistory(int limit)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT path, played_at FROM history ORDER BY played_at DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            var list = new List<(string, long)>();
            while (r.Read()) list.Add((r.GetString(0), r.GetInt64(1)));
            return list;
        }
    }

    public void ClearHistory()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM history";
            cmd.ExecuteNonQuery();
        }
    }

    // ---------- Любимые треки ----------

    /// <summary>Пути любимых треков, последние добавленные первыми.</summary>
    public List<string> LoadLikes()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT path FROM likes ORDER BY added DESC";
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    public void SetLiked(IEnumerable<string> paths, bool liked)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = liked
                ? "INSERT OR IGNORE INTO likes (path, added) VALUES ($p, $a)"
                : "DELETE FROM likes WHERE path = $p";
            var pp = cmd.Parameters.Add("$p", SqliteType.Text);
            var pa = cmd.Parameters.Add("$a", SqliteType.Integer);
            var now = DateTime.UtcNow.Ticks;
            foreach (var p in paths)
            {
                pp.Value = p;
                pa.Value = now++;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // ---------- Фото ----------

    public Dictionary<(string Kind, string Key), string> LoadImages()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT kind, key, file FROM images";
            using var r = cmd.ExecuteReader();
            var map = new Dictionary<(string, string), string>();
            while (r.Read()) map[(r.GetString(0), r.GetString(1))] = r.GetString(2);
            return map;
        }
    }

    public void SetImage(string kind, string key, string file)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO images (kind, key, file) VALUES ($k, $key, $f)
                ON CONFLICT(kind, key) DO UPDATE SET file = excluded.file
                """;
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$f", file);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Копия базы в другой файл (для переноса данных в новую папку). VACUUM INTO делает целостную копию
    /// без остановки работы. Пути к фото переписываются на новую папку картинок.
    /// </summary>
    public void CopyTo(string file, string oldImagesDir, string newImagesDir)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "VACUUM INTO $f";
            cmd.Parameters.AddWithValue("$f", file);
            cmd.ExecuteNonQuery();
        }

        using var copy = new SqliteConnection($"Data Source={file};Pooling=False");
        copy.Open();
        using var upd = copy.CreateCommand();
        upd.CommandText = "UPDATE images SET file = $new || substr(file, length($old) + 1) WHERE substr(lower(file), 1, length($old)) = lower($old)";
        upd.Parameters.AddWithValue("$old", oldImagesDir.TrimEnd('\\') + "\\");
        upd.Parameters.AddWithValue("$new", newImagesDir.TrimEnd('\\') + "\\");
        upd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
