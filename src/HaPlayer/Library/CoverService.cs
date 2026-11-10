using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace HaPlayer.Library;

/// <summary>
/// Обложки: сначала встроенная в файл картинка, потом cover.jpg / folder.jpg рядом с файлом.
/// Картинки декодируются сразу в нужную ширину, чтобы не держать в памяти оригиналы.
/// </summary>
public sealed class CoverService
{
    private static readonly string[] FolderNames = ["cover", "folder", "front", "albumart"];
    private static readonly string[] ImageExt = [".jpg", ".jpeg", ".png"];

    private readonly ConcurrentDictionary<(string, int), Task<Bitmap?>> _bitmaps = new();
    private readonly ConcurrentDictionary<string, Task<Color?>> _colors = new();

    public Task<Bitmap?> GetAsync(string trackPath, int width) =>
        _bitmaps.GetOrAdd((trackPath, width), key => Task.Run(() => Decode(key.Item1, key.Item2)));

    public Task<Color?> GetColorAsync(string trackPath) =>
        _colors.GetOrAdd(trackPath, p => Task.Run(() => DominantColor(p)));

    private static Bitmap? Decode(string trackPath, int width)
    {
        var data = ReadImage(trackPath);
        if (data == null) return null;
        try
        {
            using var ms = new MemoryStream(data);
            return Bitmap.DecodeToWidth(ms, width, BitmapInterpolationMode.HighQuality);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Путь может указывать на трек (берём его обложку) или прямо на картинку (фото, которое поставил пользователь).</summary>
    private static byte[]? ReadImage(string trackPath)
    {
        if (AppPaths.IsImage(trackPath))
        {
            try
            {
                return File.ReadAllBytes(trackPath);
            }
            catch
            {
                return null;
            }
        }

        try
        {
            using var file = TagLib.File.Create(trackPath);
            var pics = file.Tag.Pictures;
            var pic = pics.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? pics.FirstOrDefault();
            if (pic?.Data?.Data is { Length: > 0 } bytes) return bytes;
        }
        catch
        {
            // нет тегов или файл не читается
        }

        try
        {
            var dir = Path.GetDirectoryName(trackPath);
            if (dir == null) return null;
            foreach (var name in FolderNames)
            foreach (var ext in ImageExt)
            {
                var candidate = Path.Combine(dir, name + ext);
                if (File.Exists(candidate)) return File.ReadAllBytes(candidate);
            }
        }
        catch
        {
            // папка недоступна
        }

        return null;
    }

    /// <summary>Средний цвет обложки, подкрученный по насыщенности и яркости. Идёт в градиент шапки.</summary>
    private static Color? DominantColor(string trackPath)
    {
        var data = ReadImage(trackPath);
        if (data == null) return null;
        try
        {
            using var ms = new MemoryStream(data);
            using var bmp = Bitmap.DecodeToWidth(ms, 24, BitmapInterpolationMode.MediumQuality);
            var size = bmp.PixelSize;
            var stride = size.Width * 4;
            var buffer = new byte[stride * size.Height];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                bmp.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), buffer.Length, stride);
            }
            finally
            {
                handle.Free();
            }

            var rgba = bmp.Format == PixelFormat.Rgba8888;
            double r = 0, g = 0, b = 0, n = 0;
            for (var i = 0; i < buffer.Length; i += 4)
            {
                double c0 = buffer[i], c1 = buffer[i + 1], c2 = buffer[i + 2];
                var (pr, pg, pb) = rgba ? (c0, c1, c2) : (c2, c1, c0);
                var max = Math.Max(pr, Math.Max(pg, pb));
                var min = Math.Min(pr, Math.Min(pg, pb));
                // Почти чёрные, почти белые и серые пиксели дают грязный цвет, учитываем их слабее.
                var w = max < 30 || min > 225 ? 0.1 : 1 + (max - min) / 64;
                r += pr * w;
                g += pg * w;
                b += pb * w;
                n += w;
            }
            if (n == 0) return null;
            r /= n;
            g /= n;
            b /= n;

            var gray = (r + g + b) / 3;
            r = gray + (r - gray) * 1.4;
            g = gray + (g - gray) * 1.4;
            b = gray + (b - gray) * 1.4;
            var k = Math.Min(2.5, 150 / Math.Max(1, Math.Max(r, Math.Max(g, b))));
            byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v * k), 0, 255);
            return Color.FromRgb(Clamp(r), Clamp(g), Clamp(b));
        }
        catch
        {
            return null;
        }
    }
}
