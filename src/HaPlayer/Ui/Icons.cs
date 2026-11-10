using Avalonia.Media;

namespace HaPlayer.Ui;

/// <summary>
/// Значки в сетке 24×24. Линейные рисуются обводкой толщиной 2 (стиль Lucide, лицензия ISC),
/// залитые перечислены в Solid. Рисует их элемент Glyph.
/// </summary>
public static class Icons
{
    // Управление воспроизведением: залитые, со скруглёнными углами
    public static readonly Geometry Play = Parse("M7.5 4.8v14.4a.8.8 0 0 0 1.2.7l11.6-7.2a.8.8 0 0 0 0-1.4L8.7 4.1a.8.8 0 0 0-1.2.7z");
    public static readonly Geometry Pause = Parse("M6.5 4.5h3.5v15H6.5z M14 4.5h3.5v15H14z");
    public static readonly Geometry Next = Parse("M5.5 5.6v12.8a.6.6 0 0 0 .95.5l9-6.4a.6.6 0 0 0 0-1L6.45 5.1a.6.6 0 0 0-.95.5z M17.5 5h1.5v14h-1.5z");
    public static readonly Geometry Prev = Parse("M18.5 5.6v12.8a.6.6 0 0 1-.95.5l-9-6.4a.6.6 0 0 1 0-1l9-6.4a.6.6 0 0 1 .95.5z M5 5h1.5v14H5z");
    public static readonly Geometry Heart = Parse("M19 14c1.49-1.46 3-3.21 3-5.5A5.5 5.5 0 0 0 16.5 3c-1.76 0-3 .5-4.5 2-1.5-1.5-2.74-2-4.5-2A5.5 5.5 0 0 0 2 8.5c0 2.3 1.5 4.05 3 5.5l7 7z");
    public static readonly Geometry Dot = Parse("M14 12a2 2 0 1 1-4 0 2 2 0 0 1 4 0z");
    public static readonly Geometry CircleFill = Parse("M22 12a10 10 0 1 1-20 0 10 10 0 0 1 20 0z");

    // Линейные
    public static readonly Geometry HeartOutline = Parse("M19 14c1.49-1.46 3-3.21 3-5.5A5.5 5.5 0 0 0 16.5 3c-1.76 0-3 .5-4.5 2-1.5-1.5-2.74-2-4.5-2A5.5 5.5 0 0 0 2 8.5c0 2.3 1.5 4.05 3 5.5l7 7z");
    public static readonly Geometry Shuffle = Parse("M2 18h1.97a4 4 0 0 0 3.3-1.7l5.46-7.6A4 4 0 0 1 16.03 7H22 M18 3l4 4-4 4 M2 6h1.97a4 4 0 0 1 3.6 2.2 M22 17h-6.04a4 4 0 0 1-3.3-1.8l-.36-.45 M18 13l4 4-4 4");
    public static readonly Geometry Repeat = Parse("M17 2l4 4-4 4 M3 11v-1a4 4 0 0 1 4-4h14 M7 22l-4-4 4-4 M21 13v1a4 4 0 0 1-4 4H3");
    public static readonly Geometry RepeatOne = Parse("M17 2l4 4-4 4 M3 11v-1a4 4 0 0 1 4-4h14 M7 22l-4-4 4-4 M21 13v1a4 4 0 0 1-4 4H3 M11 10h1v4");
    public static readonly Geometry Volume = Parse("M11 5 6 9H3a1 1 0 0 0-1 1v4a1 1 0 0 0 1 1h3l5 4z M15.5 8.5a5 5 0 0 1 0 7 M19 5a10 10 0 0 1 0 14");
    public static readonly Geometry VolumeLow = Parse("M11 5 6 9H3a1 1 0 0 0-1 1v4a1 1 0 0 0 1 1h3l5 4z M15.5 8.5a5 5 0 0 1 0 7");
    public static readonly Geometry VolumeOff = Parse("M11 5 6 9H3a1 1 0 0 0-1 1v4a1 1 0 0 0 1 1h3l5 4z M22 9l-6 6 M16 9l6 6");
    public static readonly Geometry Home = Parse("M15 21v-8a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v8 M3 10a2 2 0 0 1 .71-1.53l7-6a2 2 0 0 1 2.58 0l7 6A2 2 0 0 1 21 10v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z");
    public static readonly Geometry Library = Parse("M16 6l4 14 M12 6v14 M8 8v12 M4 4v16");
    public static readonly Geometry Folder = Parse("M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2z");
    public static readonly Geometry Note = Parse("M9 18V5l12-2v13 M9 18a3 3 0 1 1-6 0 3 3 0 0 1 6 0z M21 16a3 3 0 1 1-6 0 3 3 0 0 1 6 0z");
    public static readonly Geometry Clock = Parse("M22 12a10 10 0 1 1-20 0 10 10 0 0 1 20 0z M12 6v6l4 2");
    public static readonly Geometry Add = Parse("M5 12h14 M12 5v14");
    public static readonly Geometry CirclePlus = Parse("M22 12a10 10 0 1 1-20 0 10 10 0 0 1 20 0z M8 12h8 M12 8v8");
    public static readonly Geometry Check = Parse("M7.5 12.5l3 3 6-6");
    public static readonly Geometry PlaylistAdd = Parse("M11 12H3 M16 6H3 M16 18H3 M18 9v6 M21 12h-6");
    public static readonly Geometry Photo = Parse("M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M11 9a2 2 0 1 1-4 0 2 2 0 0 1 4 0z M21 15l-3.09-3.09a2 2 0 0 0-2.82 0L6 21");
    public static readonly Geometry Edit = Parse("M21.17 6.81a2.82 2.82 0 0 0-3.99-3.99L3.84 16.17a2 2 0 0 0-.5.83l-1.32 4.35a.5.5 0 0 0 .62.62l4.35-1.32a2 2 0 0 0 .83-.5z");
    public static readonly Geometry Delete = Parse("M3 6h18 M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6 M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2");
    public static readonly Geometry Person = Parse("M19 21v-2a4 4 0 0 0-4-4H9a4 4 0 0 0-4 4v2 M16 7a4 4 0 1 1-8 0 4 4 0 0 1 8 0z");
    public static readonly Geometry Search = Parse("M19 11a8 8 0 1 1-16 0 8 8 0 0 1 16 0z M21 21l-4.3-4.3");
    public static readonly Geometry ChevronDown = Parse("M6 9l6 6 6-6");
    public static readonly Geometry ChevronLeft = Parse("M15 18l-6-6 6-6");
    public static readonly Geometry ChevronRight = Parse("M9 18l6-6-6-6");
    public static readonly Geometry Expand = Parse("M15 3h6v6 M9 21H3v-6 M21 3l-7 7 M3 21l7-7");
    public static readonly Geometry Equalizer = Parse("M4 21v-7 M4 10V3 M12 21v-9 M12 8V3 M20 21v-5 M20 12V3 M2 14h4 M10 8h4 M18 16h4");
    public static readonly Geometry Queue = Parse("M21 15V6 M18.5 18a2.5 2.5 0 1 0 0-5 2.5 2.5 0 0 0 0 5z M12 12H3 M16 6H3 M12 18H3");
    public static readonly Geometry Close = Parse("M18 6 6 18 M6 6l12 12");
    public static readonly Geometry Settings = Parse("M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0z");
    public static readonly Geometry More = Parse("M5 12h.01 M12 12h.01 M19 12h.01");
    public static readonly Geometry Import = Parse("M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M7 10l5 5 5-5 M12 15V3");
    public static readonly Geometry Pin = Parse("M12 17v5 M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1z");
    public static readonly Geometry Sort = Parse("M3 5h.01 M3 12h.01 M3 19h.01 M8 5h13 M8 12h13 M8 19h13");
    public static readonly Geometry Maximize = Parse("M8 3H5a2 2 0 0 0-2 2v3 M21 8V5a2 2 0 0 0-2-2h-3 M3 16v3a2 2 0 0 0 2 2h3 M16 21h3a2 2 0 0 0 2-2v-3");
    // Устройства вывода и история
    public static readonly Geometry Devices = Parse("M10 3H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h4 M14 3h4a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2h-4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M18 15a2 2 0 1 1-4 0 2 2 0 0 1 4 0z M16 7h.01");
    public static readonly Geometry Laptop = Parse("M20 16V7a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v9 M2 19h20 M3 16h18l1 3H2z");
    public static readonly Geometry Speaker = Parse("M6 2h12a2 2 0 0 1 2 2v16a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2z M16 14a4 4 0 1 1-8 0 4 4 0 0 1 8 0z M12 6h.01");
    public static readonly Geometry Headphones = Parse("M3 14h3a2 2 0 0 1 2 2v3a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-7a9 9 0 0 1 18 0v7a2 2 0 0 1-2 2h-1a2 2 0 0 1-2-2v-3a2 2 0 0 1 2-2h3");
    public static readonly Geometry Bluetooth = Parse("M7 7l10 10-5 5V2l5 5L7 17");
    public static readonly Geometry Monitor = Parse("M4 3h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M8 21h8 M12 17v4");
    public static readonly Geometry Cable = Parse("M17 21v-2a1 1 0 0 1-1-1v-1a2 2 0 0 1 2-2h2a2 2 0 0 1 2 2v1a1 1 0 0 1-1 1 M19 15V6.5a3.5 3.5 0 0 0-7 0v11a3.5 3.5 0 0 1-7 0V9 M21 21v-2h-4 M3 5h4V3 M7 5a1 1 0 0 1 1 1v1a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a1 1 0 0 1 1-1V3");
    public static readonly Geometry Refresh = Parse("M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8 M21 3v5h-5 M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16 M8 16H3v5");
    public static readonly Geometry History = Parse("M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8 M3 3v5h5 M12 7v5l4 2");
    public static readonly Geometry Lock = Parse("M5 11h14a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-7a2 2 0 0 1 2-2z M7 11V7a5 5 0 0 1 10 0v4");
    public static readonly Geometry PinOff = Parse("M12 17v5 M15 9.34V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H7.89 M2 2l20 20 M9 9v1.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h11");
    public static readonly Geometry Collapse = Parse("M4 14h6v6 M20 10h-6V4 M14 10l7-7 M3 21l7-7");
    public static readonly Geometry PanelRight = Parse("M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M15 3v18");

    private static readonly HashSet<Geometry> SolidSet = [Play, Pause, Next, Prev, Heart, Dot, CircleFill];

    /// <summary>Залитый значок или линейный.</summary>
    public static bool IsSolid(Geometry g) => SolidSet.Contains(g);

    private static Geometry Parse(string data) => StreamGeometry.Parse(data);
}
