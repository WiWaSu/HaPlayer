using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using HaPlayer.Ui;
using HaPlayer.ViewModels;

namespace HaPlayer.Views;

/// <summary>Меню луны: таймер сна, остановка через несколько треков, ручной режим. Общее для обоих видов.</summary>
public static class SleepMenu
{
    public static void Show(Control target, MainViewModel vm, PlacementMode placement)
    {
        var items = new List<Control>();
        MenuItem Item(string header, int minutes) => new() { Header = header, Command = new RelayCommand(() => vm.SetSleepTimer(minutes)) };
        foreach (var m in new[] { 15, 30, 45, 60, 90 }) items.Add(Item(L.F("{0} минут", m), m));
        items.Add(Item(L.T("После этого трека"), -1));
        var byTracks = new MenuItem { Header = L.T("Через несколько треков") };
        foreach (var n in new[] { 2, 3, 5, 10 })
        {
            var count = n;
            byTracks.Items.Add(new MenuItem { Header = Format.Tracks(n), Command = new RelayCommand(() => vm.SetStopAfterTracks(count)) });
        }
        ToolTip.SetTip(byTracks, L.T("Считая тот, что играет сейчас"));
        items.Add(byTracks);
        items.Add(new Separator());
        var manual = new MenuItem
        {
            Header = (vm.Player.ManualAdvance ? "✓ " : "") + L.T("Ручной режим"),
            Command = new RelayCommand(vm.ToggleManualAdvance),
        };
        ToolTip.SetTip(manual, L.T("Трек доигрывает и останавливается. Следующий включается по нажатию."));
        items.Add(manual);
        if (vm.IsSleepActive)
        {
            items.Add(new Separator());
            items.Add(Item(L.T("Выключить таймер сна"), 0));
        }
        new ContextMenu { ItemsSource = items, Placement = placement }.Open(target);
    }
}
