using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using HaPlayer.Ui;
using HaPlayer.ViewModels;

namespace HaPlayer.Views;

public partial class PlayerBarView : UserControl
{
    public PlayerBarView()
    {
        InitializeComponent();
        // Правый клик по играющему треку: показать в медиатеке, исполнитель, радио, сведения.
        TrackButton.ContextRequested += (_, e) =>
        {
            if (DataContext is not MainViewModel vm || vm.Player.CurrentTrack is not { } t) return;
            var items = new List<Control>
            {
                new MenuItem { Header = L.T("Показать в медиатеке"), Command = vm.RevealCurrentCommand },
                new MenuItem { Header = L.T("Перейти к исполнителю"), Command = new RelayCommand(() => vm.OpenArtist(t.Artist)) },
                new MenuItem { Header = L.T("Радио по треку"), Command = new RelayCommand(() => vm.PlayRadio(t)) },
                new Separator(),
                new MenuItem { Header = L.T("Сведения о файле"), Command = new AsyncRelayCommand(() => vm.ShowFileInfoAsync(t)) },
            };
            new ContextMenu { ItemsSource = items, Placement = PlacementMode.Pointer }.Open(TrackButton);
            e.Handled = true;
        };

        // Таймер сна: меню с вариантами по щелчку на луну.
        SleepButton.Click += (_, _) =>
        {
            if (DataContext is MainViewModel vm) SleepMenu.Show(SleepButton, vm, PlacementMode.TopEdgeAlignedRight);
        };
    }
}
