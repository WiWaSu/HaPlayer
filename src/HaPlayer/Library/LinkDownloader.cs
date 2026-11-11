using HaPlayer.Ui;
using System.Net.Http.Headers;
using System.Text;

namespace HaPlayer.Library;

/// <summary>Ошибка, которую можно показать пользователю как есть.</summary>
public sealed class LinkException(string message) : Exception(message);

/// <summary>
/// Скачивает аудиофайл по прямой ссылке в папку медиатеки.
/// Работает с любым сайтом одинаково: никаких правил под конкретные сайты, никакого разбора страниц.
/// Если по ссылке открывается веб-страница, сообщает об этом.
/// </summary>
public static class LinkDownloader
{
    // Больше этого не качаем: защита от бесконечных потоков и случайных ссылок на образы дисков.
    private const long MaxBytes = 1024L * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan, // общий таймаут задаёт вызывающий код через токен
        };
        var version = typeof(LinkDownloader).Assembly.GetName().Version?.ToString(2) ?? "1";
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"HaPlayer/{version}");
        http.DefaultRequestHeaders.Accept.ParseAdd("audio/*, */*;q=0.5");
        return http;
    }

    private static readonly Dictionary<string, string> ExtByMime = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio/mpeg"] = ".mp3",
        ["audio/mp3"] = ".mp3",
        ["audio/mpeg3"] = ".mp3",
        ["audio/x-mpeg-3"] = ".mp3",
        ["audio/flac"] = ".flac",
        ["audio/x-flac"] = ".flac",
        ["audio/wav"] = ".wav",
        ["audio/x-wav"] = ".wav",
        ["audio/wave"] = ".wav",
        ["audio/vnd.wave"] = ".wav",
        ["audio/ogg"] = ".ogg",
        ["audio/vorbis"] = ".ogg",
        ["application/ogg"] = ".ogg",
        ["audio/mp4"] = ".m4a",
        ["audio/x-m4a"] = ".m4a",
        ["audio/m4a"] = ".m4a",
        ["audio/aac"] = ".aac",
        ["audio/aacp"] = ".aac",
        ["audio/x-aac"] = ".aac",
        ["audio/x-ms-wma"] = ".wma",
    };

    private static readonly string[] PlaylistMimes =
        ["audio/x-mpegurl", "audio/mpegurl", "application/vnd.apple.mpegurl", "application/x-mpegurl", "audio/x-scpls"];

    private static readonly string[] PlaylistExts = [".m3u", ".m3u8", ".pls", ".xspf"];

    /// <summary>Достаёт ссылки http(s) из вставленного текста: по одной на строку или через пробел.</summary>
    public static List<Uri> ParseLinks(string text)
    {
        var result = new List<Uri>();
        foreach (var part in text.Split([' ', '\t', '\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var s = part.Trim().Trim('"', '\'', '<', '>');
            if (Uri.TryCreate(s, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                !result.Contains(uri))
            {
                result.Add(uri);
            }
        }
        return result;
    }

    /// <summary>Скачивает файл и возвращает путь к нему на диске.</summary>
    public static async Task<string> DownloadAsync(Uri url, string folder, IProgress<(long Done, long? Total)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);

        HttpResponseMessage response;
        try
        {
            response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new LinkException(L.F("Не удалось подключиться к {0}. Проверьте интернет и саму ссылку.\n({1})", url.Host, ex.Message));
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new LinkException((int)response.StatusCode switch
                {
                    404 => L.T("По этой ссылке ничего нет (ошибка 404). Возможно, файл удалили или ссылка устарела."),
                    401 or 403 => L.F("Сайт не отдаёт файл (ошибка {0}). Часто так бывает, когда файл доступен только после входа или только в браузере.", (int)response.StatusCode),
                    _ => L.F("Сайт ответил ошибкой {0}.", (int)response.StatusCode),
                });
            }

            var content = response.Content.Headers;
            var mime = content.ContentType?.MediaType ?? "";
            var finalUrl = response.RequestMessage?.RequestUri ?? url;
            var serverName = FileNameFromHeader(content.ContentDisposition);
            var urlName = Uri.UnescapeDataString(Path.GetFileName(finalUrl.AbsolutePath));

            if (mime.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
                mime.StartsWith("application/xhtml", StringComparison.OrdinalIgnoreCase))
                throw NotAFile();

            if (PlaylistMimes.Contains(mime, StringComparer.OrdinalIgnoreCase) ||
                PlaylistExts.Contains(Path.GetExtension(serverName ?? urlName), StringComparer.OrdinalIgnoreCase))
                throw new LinkException(L.T("Это ссылка на плейлист или интернет-радио. Скачать такое пока нельзя: нужна ссылка на отдельный аудиофайл."));

            // Интернет-радио отдаёт бесконечный поток audio/mpeg без размера и с заголовками icy-*.
            if (content.ContentLength == null && response.Headers.Any(h => h.Key.StartsWith("icy-", StringComparison.OrdinalIgnoreCase)))
                throw new LinkException(L.T("Это поток интернет-радио, у него нет конца, поэтому скачать его нельзя."));

            if (content.ContentLength > MaxBytes)
                throw new LinkException(L.T("Файл больше 1 ГБ. Для одной песни это слишком много, похоже, ссылка ведёт не на трек."));

            var ext = PickExtension(serverName, urlName, mime)
                      ?? throw new LinkException(
                          L.F("По ссылке не аудиофайл или формат, который HaPlayer пока не умеет играть (тип: {0}).", mime.Length > 0 ? mime : L.T("не указан")) + "\n" +
                          L.F("Поддерживаются: {0}.", string.Join(", ", AudioFormats.Extensions)));

            var baseName = MakeSafeName(Path.GetFileNameWithoutExtension(serverName ?? urlName));
            if (baseName.Length == 0) baseName = L.T("Трек ") + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
            var target = UniquePath(folder, baseName, ext);
            var part = target + ".part";

            try
            {
                await using (var src = await response.Content.ReadAsStreamAsync(ct))
                await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    long done = 0;
                    var first = true;
                    int read;
                    while ((read = await src.ReadAsync(buffer, ct)) > 0)
                    {
                        // Сервер мог назвать страницу аудиофайлом. Проверяем по первым байтам.
                        if (first)
                        {
                            first = false;
                            if (LooksLikeHtml(buffer.AsSpan(0, read))) throw NotAFile();
                        }
                        done += read;
                        if (done > MaxBytes)
                            throw new LinkException(L.T("Скачано больше 1 ГБ, а файл всё не кончается. Похоже, это бесконечный поток."));
                        await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                        progress?.Report((done, content.ContentLength));
                    }
                    if (done == 0) throw new LinkException(L.T("Сайт вернул пустой файл."));
                }
                File.Move(part, target);
                return target;
            }
            catch
            {
                TryDelete(part);
                throw;
            }
        }
    }

    private static LinkException NotAFile() => new(
        L.T("По ссылке открывается страница сайта.\n\n") +
        L.T("Нужна прямая ссылка на аудиофайл. Обычно её можно получить так: правой кнопкой по кнопке «Скачать» на сайте → «Копировать адрес ссылки». ") +
        L.T("Такая ссылка чаще всего заканчивается на .mp3 или .flac."));

    private static string? PickExtension(string? serverName, string urlName, string mime)
    {
        foreach (var name in new[] { serverName, urlName })
        {
            var ext = Path.GetExtension(name ?? "");
            if (ext.Length > 0 && AudioFormats.IsSupported(ext)) return ext.ToLowerInvariant();
        }
        return ExtByMime.TryGetValue(mime, out var byMime) ? byMime : null;
    }

    private static string? FileNameFromHeader(ContentDispositionHeaderValue? cd)
    {
        var name = cd?.FileNameStar ?? cd?.FileName;
        return string.IsNullOrWhiteSpace(name) ? null : Path.GetFileName(name.Trim('"'));
    }

    private static bool LooksLikeHtml(ReadOnlySpan<byte> head)
    {
        var text = Encoding.ASCII.GetString(head[..Math.Min(head.Length, 256)]).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return text.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("<head", StringComparison.OrdinalIgnoreCase);
    }

    private static string MakeSafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(invalid.Contains(c) ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.');
        return s.Length > 120 ? s[..120] : s;
    }

    private static string UniquePath(string folder, string baseName, string ext)
    {
        var path = Path.Combine(folder, baseName + ext);
        for (var i = 2; File.Exists(path) || File.Exists(path + ".part"); i++)
            path = Path.Combine(folder, $"{baseName} ({i}){ext}");
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // недокачанный файл останется с расширением .part, в медиатеку он не попадёт
        }
    }
}
