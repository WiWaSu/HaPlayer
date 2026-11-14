using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using HaPlayer.ViewModels;

namespace HaPlayer.Views;

/// <summary>
/// Запоминает прокрутку страницы в её модели и восстанавливает при возврате.
/// В больших списках («Все треки», плейлисты) после «Назад» не нужно листать заново.
/// </summary>
public static class ScrollMemory
{
    public static void Attach(Control view)
    {
        ScrollViewer? scroller = null;
        view.AttachedToVisualTree += (_, _) =>
        {
            // Список с виртуализацией строит строки не сразу: восстанавливаем после первой раскладки.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                scroller = view.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                if (scroller != null && view.DataContext is PageViewModel page && page.ScrollOffset != default)
                    scroller.Offset = page.ScrollOffset;
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        };
        view.DetachedFromVisualTree += (_, _) =>
        {
            if (scroller != null && view.DataContext is PageViewModel page) page.ScrollOffset = scroller.Offset;
        };
    }
}
