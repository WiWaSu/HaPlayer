using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HaPlayer.Library;
using HaPlayer.Ui;
using HaPlayer.ViewModels;

namespace HaPlayer.Views;

/// <summary>
/// Окно умного плейлиста: название, «все условия» или «любое», список условий.
/// Пока условия меняются, внизу видно, сколько треков подходит.
/// </summary>
public static class SmartEditor
{
    public static async Task<SmartPlaylistDef?> ShowAsync(Window owner, SmartPlaylistDef source, Func<SmartPlaylistDef, int> count)
    {
        // Правим копию: «Отмена» не должна менять сохранённый плейлист.
        var def = new SmartPlaylistDef
        {
            Id = source.Id,
            Name = source.Name,
            MatchAll = source.MatchAll,
            Rules = source.Rules.Select(r => new SmartRule { Field = r.Field, Op = r.Op, Value = r.Value }).ToList(),
        };
        if (def.Rules.Count == 0) def.Rules.Add(new SmartRule { Field = "genre", Op = "contains" });

        SmartPlaylistDef? result = null;
        var dialog = new Window
        {
            Title = L.T("Умный плейлист"),
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            SystemDecorations = SystemDecorations.None,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
        };
        dialog.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(dialog).Properties.IsLeftButtonPressed && e.Source is Border or StackPanel or TextBlock)
                dialog.BeginMoveDrag(e);
        };

        var name = new TextBox { Text = def.Name, Watermark = L.T("Название"), FontSize = 15 };
        var mode = new ComboBox
        {
            ItemsSource = new[] { L.T("Все условия (И)"), L.T("Любое условие (ИЛИ)") },
            SelectedIndex = def.MatchAll ? 0 : 1,
            MinWidth = 220,
        };
        var rules = new StackPanel { Spacing = 8 };
        var counter = new TextBlock { Foreground = ThemeService.Brush("SubBrush"), FontSize = 13 };

        void Recount()
        {
            def.MatchAll = mode.SelectedIndex != 1;
            counter.Text = L.F("Подходит: {0}", Format.Tracks(count(def)));
        }

        void Rebuild()
        {
            rules.Children.Clear();
            foreach (var rule in def.Rules.ToList()) rules.Children.Add(Row(rule));
            Recount();
        }

        Control Row(SmartRule rule)
        {
            var fields = SmartFilter.Fields;
            var field = new ComboBox
            {
                ItemsSource = fields.Select(f => f.Label).ToList(),
                SelectedIndex = Math.Max(0, fields.ToList().FindIndex(f => f.Key == rule.Field)),
                Width = 200,
            };
            var op = new ComboBox { Width = 140 };
            var value = new TextBox { Text = rule.Value, Width = 160 };
            var remove = new Button { Content = new Glyph { Data = Icons.Close, Width = 14, Height = 14 }, VerticalAlignment = VerticalAlignment.Center };
            remove.Classes.Add("icon");
            remove.Classes.Add("hoverbg");
            ToolTip.SetTip(remove, L.T("Убрать условие"));

            void FillOps()
            {
                var kind = SmartFilter.Field(rule.Field).Kind;
                var ops = SmartFilter.Ops(kind);
                op.ItemsSource = ops.Select(o => o.Label).ToList();
                var at = ops.ToList().FindIndex(o => o.Key == rule.Op);
                if (at < 0)
                {
                    at = 0;
                    rule.Op = ops[0].Key;
                }
                op.SelectedIndex = at;
                value.IsVisible = kind != SmartFieldKind.Flag;
                value.Watermark = kind == SmartFieldKind.Number ? "0" : L.T("Текст");
            }

            FillOps();
            field.SelectionChanged += (_, _) =>
            {
                if (field.SelectedIndex < 0) return;
                rule.Field = fields[field.SelectedIndex].Key;
                FillOps();
                Recount();
            };
            op.SelectionChanged += (_, _) =>
            {
                var ops = SmartFilter.Ops(SmartFilter.Field(rule.Field).Kind);
                if (op.SelectedIndex >= 0 && op.SelectedIndex < ops.Count) rule.Op = ops[op.SelectedIndex].Key;
                Recount();
            };
            value.TextChanged += (_, _) =>
            {
                rule.Value = value.Text ?? "";
                Recount();
            };
            remove.Click += (_, _) =>
            {
                def.Rules.Remove(rule);
                Rebuild();
            };
            return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { field, op, value, remove } };
        }

        mode.SelectionChanged += (_, _) => Recount();

        var add = new Button { Content = L.T("Добавить условие"), HorizontalAlignment = HorizontalAlignment.Left };
        add.Classes.Add("pill");
        add.Classes.Add("ghost");
        add.Click += (_, _) =>
        {
            def.Rules.Add(new SmartRule { Field = "artist", Op = "contains" });
            Rebuild();
        };

        var ok = new Button { Content = L.T("Сохранить"), MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        ok.Classes.Add("pill");
        var cancel = new Button { Content = L.T("Отмена"), MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
        cancel.Classes.Add("pill");
        cancel.Classes.Add("ghost");
        ok.Click += (_, _) =>
        {
            def.Name = string.IsNullOrWhiteSpace(name.Text) ? L.T("Умный плейлист") : name.Text.Trim();
            def.MatchAll = mode.SelectedIndex != 1;
            def.Rules = def.Rules.Where(SmartFilter.IsComplete).ToList();
            result = def;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();

        Rebuild();
        dialog.Content = new Border
        {
            Background = ThemeService.Brush("MenuBrush"),
            BorderBrush = ThemeService.Brush("Ov1A"),
            BorderThickness = new Thickness(1),
            CornerRadius = ThemeService.Radius(12),
            Child = new StackPanel
            {
                Margin = new Thickness(28, 24),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = L.T("Умный плейлист"), FontSize = 22, FontWeight = FontWeight.Bold },
                    name,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock { Text = L.T("Подходят треки, где выполняются"), VerticalAlignment = VerticalAlignment.Center },
                            mode,
                        },
                    },
                    rules,
                    add,
                    counter,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 12,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, ok },
                    },
                },
            },
        };
        dialog.Opened += (_, _) =>
        {
            name.Focus();
            name.SelectAll();
        };
        await dialog.ShowDialog(owner);
        return result;
    }
}
