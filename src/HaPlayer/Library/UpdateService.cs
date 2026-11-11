using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HaPlayer.Library;

/// <summary>
/// Обновление с GitHub: найти релиз новее, скачать его HaPlayer.exe, проверить и поставить на место текущего.
/// Сеть трогается только по кнопке или если пользователь включил проверку при запуске.
/// </summary>
public static class UpdateService
{
    /// <summary>Репозиторий на GitHub в виде «логин/имя».</summary>
    public const string GitHubRepo = "WiWaSu/HaPlayer";

    public static bool HasRepo => !string.IsNullOrWhiteSpace(GitHubRepo);
    public static string RepoUrl => $"https://github.com/{GitHubRepo}";
    public static string ReleasesUrl => $"{RepoUrl}/releases";

    /// <summary>Версия этой сборки, например «1.5».</summary>
    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0";

    /// <summary>Релиз на GitHub. ExeUrl — ссылка на HaPlayer.exe, Sha256 — его контрольная сумма (если GitHub её дал).</summary>
    public sealed record Release(string Version, string Url, string? ExeUrl, string? Sha256, long Size);

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HaPlayer", CurrentVersion));
        return http;
    }

    /// <summary>Последний релиз, если он новее этой версии. null — новее нет или проверить не удалось.</summary>
    public static async Task<Release?> CheckAsync(CancellationToken ct = default)
    {
        if (!HasRepo) return null;
        try
        {
            using var http = CreateClient(TimeSpan.FromSeconds(10));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            await using var stream = await http.GetStreamAsync($"https://api.github.com/repos/{GitHubRepo}/releases/latest", ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            // Номер берём из любой метки: «v1.5», «1.5», «release-1.5-windows».
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var m = Regex.Match(tag, @"\d+(\.\d+){1,3}");
            if (!m.Success || !IsNewer(m.Value, CurrentVersion)) return null;

            var url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesUrl : ReleasesUrl;

            // Файл обновления: HaPlayer.exe, иначе любой .exe из релиза.
            string? exeUrl = null, sha = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets))
            {
                var exe = assets.EnumerateArray()
                    .Where(a => (a.GetProperty("name").GetString() ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(a => string.Equals(a.GetProperty("name").GetString(), "HaPlayer.exe", StringComparison.OrdinalIgnoreCase))
                    .Cast<JsonElement?>()
                    .FirstOrDefault();
                if (exe is { } a)
                {
                    exeUrl = a.GetProperty("browser_download_url").GetString();
                    size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    if (a.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:"))
                        sha = digest["sha256:".Length..];
                }
            }
            return new Release(m.Value, url, exeUrl, sha, size);
        }
        catch
        {
            // нет сети, нет релизов, GitHub ограничил запросы: просто не показываем обновление
            return null;
        }
    }

    /// <summary>Можно ли обновиться на месте: есть exe в релизе и папка программы доступна для записи.</summary>
    public static bool CanSelfUpdate(Release r) =>
        r.ExeUrl != null && Environment.ProcessPath is { } exe && IsWritable(Path.GetDirectoryName(exe)!);

    /// <summary>
    /// Скачивает новый exe рядом с текущим (тот же диск — замена потом мгновенная) и сверяет SHA-256.
    /// Возвращает путь к скачанному файлу.
    /// </summary>
    public static async Task<string> DownloadAsync(Release r, IProgress<double> progress, CancellationToken ct = default)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось узнать, где лежит HaPlayer.exe");
        var target = exe + ".update";
        if (File.Exists(target)) File.Delete(target);

        using var http = CreateClient(TimeSpan.FromMinutes(30));
        using var response = await http.GetAsync(r.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? r.Size;

        using var sha = SHA256.Create();
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = File.Create(target))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                sha.TransformBlock(buffer, 0, read, null, 0);
                done += read;
                if (total > 0) progress.Report(done * 100.0 / total);
            }
            sha.TransformFinalBlock([], 0, 0);
        }

        // Скачалось не то или не целиком: файл не ставим.
        if (r.Sha256 != null && !string.Equals(Convert.ToHexString(sha.Hash!), r.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(target);
            throw new InvalidDataException("Скачанный файл не совпал с контрольной суммой на GitHub. Обновление не установлено.");
        }
        return target;
    }

    /// <summary>
    /// Ставит скачанный exe на место текущего и запускает его. Работающий exe Windows переименовать разрешает:
    /// он становится HaPlayer.exe.old и удаляется при следующем запуске.
    /// </summary>
    public static void ApplyAndRestart(string downloaded, Action shutdown)
    {
        var exe = Environment.ProcessPath!;
        var old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try
        {
            File.Move(downloaded, exe);
        }
        catch
        {
            // Не получилось поставить новый: возвращаем старый, чтобы программа осталась рабочей.
            File.Move(old, exe);
            throw;
        }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        shutdown();
    }

    /// <summary>При запуске после обновления убирает старый exe и недокачанные файлы.</summary>
    public static void CleanupAfterUpdate()
    {
        if (Environment.ProcessPath is not { } exe) return;
        foreach (var leftover in new[] { exe + ".old", exe + ".update" })
        {
            try
            {
                if (File.Exists(leftover)) File.Delete(leftover);
            }
            catch
            {
                // прежний процесс ещё не закрылся: удалим в следующий раз
            }
        }
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".haplayer-update-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsNewer(string candidate, string current) =>
        Version.TryParse(Pad(candidate), out var a) && Version.TryParse(Pad(current), out var b) && a > b;

    private static string Pad(string v) => v.Contains('.') ? v : v + ".0";
}
