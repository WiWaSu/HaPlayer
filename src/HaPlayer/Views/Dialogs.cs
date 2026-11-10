using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace HaPlayer.Views;

/// <summary>Небольшие модальные окна в стиле приложения: ввод названия, подтверждение, сообщение.</summary>
public static class Dialogs
{
    public static async Task<string?> PromptAsync(Window owner, string title, string placeholder, string initial, string okText)
    {
        var box = new TextBox { Text = initial, Watermark = placeholder, MinWidth = 360, FontSize = 15 };
        string? result = null;
        var dialog = Create(title);
        var ok = Button(okText, primary: true);
        var cancel = Button("Отмена", primary: false);
        ok.IsDefault = true;
        cancel.IsCancel = true;
        ok.Click += (_, _) =>
        {
            result = box.Text?.Trim();
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();

        dialog.Content = Layout(title, box, cancel, ok);
        dialog.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        await dialog.ShowDialog(owner);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string okText)
    {
        var result = false;
        var dialog = Create(title);
        var ok = Button(okText, primary: true);
        var cancel = Button("Отмена", primary: false);
        ok.IsDefault = true;
        cancel.IsCancel = true;
        ok.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = Layout(title, Text(message), cancel, ok);
        await dialog.ShowDialog(owner);
        return result;
    }

    public static async Task MessageAsync(Window owner, string title, string message)
    {
        var dialog = Create(title);
        var ok = Button("Хорошо", primary: true);
        ok.IsDefault = true;
        ok.IsCancel = true;
        ok.Click += (_, _) => dialog.Close();
        dialog.Content = Layout(title, Text(message), ok);
        await dialog.ShowDialog(owner);
    }

    /// <summary>Окно без системной рамки: тёмная карточка со скруглением. Перетаскивается за любое место.</summary>
    private static Window Create(string title)
    {
        var w = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            SystemDecorations = SystemDecorations.None,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],

        };
        w.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(w).Properties.IsLeftButtonPressed && e.Source is not TextBox) w.BeginMoveDrag(e);
        };
        return w;
    }

    private static TextBlock Text(string message) => new()
    {
        Text = message,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 420,
        Foreground = new SolidColorBrush(Color.Parse("#B3B3B3")),
        FontSize = 14,
        TextTrimming = TextTrimming.None,
    };

    private static Button Button(string text, bool primary)
    {
        var b = new Button { Content = text, MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
        b.Classes.Add("pill");
        if (!primary) b.Classes.Add("ghost");
        return b;
    }

    private static Border Layout(string title, Control body, params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in buttons) row.Children.Add(b);
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#282828")),
            BorderBrush = new SolidColorBrush(Color.Parse("#1AFFFFFF")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            MinWidth = 380,
            Child = new StackPanel
            {
                Margin = new Thickness(28, 24),
                Spacing = 18,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeight.Bold },
                    body,
                    row,
                },
            },
        };
    }
}
