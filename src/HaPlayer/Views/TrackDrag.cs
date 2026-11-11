using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HaPlayer.ViewModels;

namespace HaPlayer.Views;

/// <summary>
/// Перетаскивание треков внутри окна: из любого списка треков в плейлист или «Любимые» слева
/// и в очередь справа. Треки передаются объектами, файлы на диске не трогаются.
/// </summary>
public static class TrackDrag
{
    /// <summary>Формат данных перетаскивания. Только внутри процесса HaPlayer.</summary>
    public const string Format = "haplayer/tracks";

    private const double Threshold = 6;

    /// <summary>Строки списка можно тащить мышью. Выделенные вместе, если тащат одну из них.</summary>
    public static void AttachSource(ListBox list)
    {
        Point? start = null;
        List<TrackItem>? payload = null;

        // Туннель: выделение снимаем до того, как ListBox сбросит его на одну строку.
        list.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            start = null;
            if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) return;
            if ((e.Source as Control)?.DataContext is not TrackRow row) return;
            var selected = list.SelectedItems?.OfType<TrackRow>().OrderBy(r => r.Index).ToList() ?? [];
            payload = selected.Contains(row) && selected.Count > 1
                ? selected.Select(r => r.Item).ToList()
                : [row.Item];
            start = e.GetPosition(list);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        list.AddHandler(InputElement.PointerReleasedEvent, (_, _) => start = null,
            RoutingStrategies.Tunnel, handledEventsToo: true);

        list.AddHandler(InputElement.PointerMovedEvent, async (_, e) =>
        {
            if (start is not { } from || payload == null) return;
            if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed)
            {
                start = null;
                return;
            }
            var d = e.GetPosition(list) - from;
            if (Math.Abs(d.X) < Threshold && Math.Abs(d.Y) < Threshold) return;

            start = null;
            var data = new DataObject();
            data.Set(Format, payload);
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public static List<TrackItem>? GetTracks(DragEventArgs e) => e.Data.Get(Format) as List<TrackItem>;

    /// <summary>
    /// Принимает треки. accept решает по элементу под курсором, можно ли бросить сюда;
    /// drop выполняет действие. Подсветка цели — класс dragtarget на контроле.
    /// </summary>
    public static void AttachTarget(Control target, Func<object?, bool> accept, Action<object?, List<TrackItem>> drop)
    {
        DragDrop.SetAllowDrop(target, true);
        Control? lit = null;

        void Light(Control? c)
        {
            if (lit == c) return;
            lit?.Classes.Remove("dragtarget");
            lit = c;
            lit?.Classes.Add("dragtarget");
        }

        static Control? Container(object? source)
        {
            for (var c = source as Control; c != null; c = c.Parent as Control)
                if (c is ListBoxItem) return c;
            return null;
        }

        target.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (GetTracks(e) == null) return;
            var ctx = (e.Source as Control)?.DataContext;
            var ok = accept(ctx);
            e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            Light(ok ? Container(e.Source) ?? target : null);
            e.Handled = true;
        });
        target.AddHandler(DragDrop.DragLeaveEvent, (_, _) => Light(null));
        target.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            Light(null);
            if (GetTracks(e) is not { } tracks) return;
            var ctx = (e.Source as Control)?.DataContext;
            if (accept(ctx)) drop(ctx, tracks);
            e.Handled = true;
        });
    }
}
