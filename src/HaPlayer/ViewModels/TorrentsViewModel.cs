using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlayer.Library;
using MonoTorrent;
using MonoTorrent.Client;

namespace HaPlayer.ViewModels;

/// <summary>Строка раздачи в настройках. Состояние обновляется раз в секунду.</summary>
public sealed partial class TorrentRow : ObservableObject
{
    private readonly TorrentsViewModel _owner;

    public TorrentRow(TorrentManager manager, TorrentsViewModel owner)
    {
        Manager = manager;
        _owner = owner;
        Refresh();
    }

    public TorrentManager Manager { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private string _copyText = "Скопировать ссылку";

    public void Refresh()
    {
        var m = Manager;
        Name = m.Torrent?.Name ?? m.MagnetLink?.Name ?? "Раздача без названия";
        Progress = m.HasMetadata ? m.PartialProgress : 0;
        IsDone = TorrentService.IsFinished(m);
        Status = _owner.IsBrokenShare(m)
            ? "Часть файлов в папке изменилась или пропала, раздаётся только то, что совпало. Чтобы раздать заново, уберите раздачу и создайте её ещё раз."
            : m.State switch
        {
            TorrentState.Error => "Ошибка: " + (m.Error?.Exception.Message ?? "неизвестная"),
            TorrentState.Stopped or TorrentState.Stopping when m.HasMetadata && !TorrentService.HasMusic(m)
                => "Остановлено: в раздаче нет музыки, которую умеет играть HaPlayer",
            TorrentState.Stopped or TorrentState.Stopping or TorrentState.Paused => "Остановлено",
            TorrentState.Metadata => $"Ищу участников и список файлов… На связи: {m.OpenConnections}",
            TorrentState.Hashing => "Проверяю файлы…",
            TorrentState.Seeding or TorrentState.Downloading when IsDone => $"Раздаётся · отдано {Size(m.Monitor.DataBytesSent)} · сейчас качают: {m.UploadingTo}",
            TorrentState.Downloading => $"Скачано {m.PartialProgress:0}% · {Size(m.Monitor.DownloadRate)}/с · на связи: {m.OpenConnections}",
            _ => "Запускаю…",
        };
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:0} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.0} МБ",
        _ => $"{bytes / 1073741824.0:0.00} ГБ",
    };

    [RelayCommand]
    private Task CopyMagnet() => _owner.CopyMagnetAsync(this);

    [RelayCommand]
    private void OpenFolder() => _owner.OpenFolder(this);

    [RelayCommand]
    private Task Remove() => _owner.RemoveAsync(this);
}

/// <summary>Торренты в настройках: включить/выключить, добавить раздачу, раздать свою папку.</summary>
public sealed partial class TorrentsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly HashSet<TorrentManager> _imported = [];
    private TorrentService? _service;
    private bool _importing;

    public TorrentsViewModel(MainViewModel main)
    {
        _main = main;
        _isEnabled = main.Settings.TorrentsEnabled;
        _timer.Tick += (_, _) => Tick();
    }

    public ObservableCollection<TorrentRow> Rows { get; } = [];

    public bool IsBrokenShare(TorrentManager m) => _service?.IsBrokenShare(m) == true;
    public bool HasRows => Rows.Count > 0;

    [ObservableProperty] private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        _main.Settings.TorrentsEnabled = value;
        _main.Settings.Save();
        _ = value ? StartAsync() : StopAsync();
    }

    /// <summary>При запуске плеера: если торренты были включены, продолжаем раздачи и скачивания.</summary>
    public Task StartIfEnabledAsync() => IsEnabled ? StartAsync() : Task.CompletedTask;

    private async Task StartAsync()
    {
        if (_service != null) return;
        var service = new TorrentService();
        try
        {
            await service.StartAsync();
        }
        catch (Exception ex)
        {
            if (_main.Dialogs != null) await _main.Dialogs.MessageAsync("Торренты", "Не получилось запустить торренты: " + ex.Message);
            IsEnabled = false;
            return;
        }
        _service = service;
        // Что было докачано до перезапуска, уже лежит в медиатеке.
        foreach (var m in service.Torrents)
            if (TorrentService.IsFinished(m)) _imported.Add(m);
        Sync();
        _timer.Start();
    }

    private async Task StopAsync()
    {
        _timer.Stop();
        var service = _service;
        _service = null;
        Rows.Clear();
        OnPropertyChanged(nameof(HasRows));
        if (service != null) await service.StopAsync();
    }

    /// <summary>При выходе из плеера: сохранить список раздач. Ждём не дольше нескольких секунд.</summary>
    public void Shutdown()
    {
        _timer.Stop();
        var service = _service;
        _service = null;
        if (service == null) return;
        try
        {
            Task.Run(service.StopAsync).Wait(TimeSpan.FromSeconds(6));
        }
        catch
        {
            // не успели сохранить: раздачи придётся добавить заново
        }
    }

    private void Tick()
    {
        if (_service == null) return;
        Sync();
        foreach (var r in Rows) r.Refresh();
        _ = ImportFinishedAsync();
    }

    private void Sync()
    {
        if (_service == null) return;
        var managers = _service.Torrents;
        foreach (var gone in Rows.Where(r => !managers.Contains(r.Manager)).ToList()) Rows.Remove(gone);
        foreach (var m in managers)
            if (Rows.All(r => r.Manager != m)) Rows.Add(new TorrentRow(m, this));
        OnPropertyChanged(nameof(HasRows));
    }

    /// <summary>Докачанную музыку добавляем в медиатеку. Раздача при этом продолжается.</summary>
    private async Task ImportFinishedAsync()
    {
        if (_importing || _main.IsBusy) return;
        var done = Rows.Select(r => r.Manager).Where(m => TorrentService.IsFinished(m) && !_imported.Contains(m)).ToList();
        if (done.Count == 0) return;

        _importing = true;
        try
        {
            foreach (var m in done)
            {
                _imported.Add(m);
                var files = TorrentService.MusicFiles(m);
                if (files.Count == 0) continue;
                _main.RememberFolder(m.SavePath);
                await _main.ImportAsync(files);
            }
        }
        finally
        {
            _importing = false;
        }
    }

    // ---------- Кнопки ----------

    [RelayCommand]
    private async Task AddMagnet()
    {
        var d = _main.Dialogs;
        if (d == null || _service == null) return;
        var text = await d.PromptAsync("Открыть magnet-ссылку", "magnet:?xt=urn:btih:…", "", "Скачать");
        if (text == null) return;
        if (!TorrentService.TryParseMagnet(text, out var magnet))
        {
            await d.MessageAsync("Торренты", "Не вижу magnet-ссылки. Она начинается с magnet:?xt=…");
            return;
        }
        await RunAsync(() => _service.AddMagnetAsync(magnet, _main.DownloadFolder));
    }

    [RelayCommand]
    private async Task AddTorrentFile()
    {
        var d = _main.Dialogs;
        if (d == null || _service == null) return;
        var path = await d.PickTorrentFileAsync();
        if (path == null) return;
        await RunAsync(() => _service.AddTorrentFileAsync(path, _main.DownloadFolder));
    }

    [RelayCommand]
    private async Task ShareFolder()
    {
        var d = _main.Dialogs;
        var service = _service;
        if (d == null || service == null || _main.IsBusy) return;
        var folder = await d.PickFolderAsync();
        if (folder == null) return;

        string magnet;
        _main.IsBusy = true;
        _main.BusyText = "Готовлю раздачу: считаю контрольные суммы файлов…";
        try
        {
            var (manager, link) = await Task.Run(() => service.ShareAsync(folder));
            _imported.Add(manager); // это свои файлы, добавлять в медиатеку нечего
            magnet = link;
            Sync();
        }
        catch (Exception ex)
        {
            _main.IsBusy = false;
            await d.MessageAsync("Раздать папку", ErrorText(ex));
            return;
        }
        finally
        {
            _main.IsBusy = false;
        }

        await d.CopyTextAsync(magnet);
        await d.MessageAsync("Раздача готова",
            "Ссылка скопирована. Отправьте её другу: он откроет её в HaPlayer (Настройки → Торренты) или в любом торрент-клиенте, например qBittorrent.\n\n" +
            "Раздача идёт, пока HaPlayer открыт и торренты включены. Файлы раздаются прямо из вашей папки, ничего не копируется. " +
            "Первое подключение может занять несколько минут: участникам нужно время, чтобы найти друг друга.");
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
            Sync();
        }
        catch (Exception ex)
        {
            if (_main.Dialogs != null) await _main.Dialogs.MessageAsync("Торренты", ErrorText(ex));
        }
    }

    private static string ErrorText(Exception ex) => ex is LinkException ? ex.Message : "Не получилось: " + ex.Message;

    // ---------- Действия со строкой ----------

    public async Task CopyMagnetAsync(TorrentRow row)
    {
        if (_main.Dialogs == null) return;
        await _main.Dialogs.CopyTextAsync(TorrentService.MagnetFor(row.Manager));
        row.CopyText = "Скопировано";
        await Task.Delay(2000);
        row.CopyText = "Скопировать ссылку";
    }

    public void OpenFolder(TorrentRow row)
    {
        var m = row.Manager;
        var dir = Directory.Exists(m.ContainingDirectory) ? m.ContainingDirectory : m.SavePath;
        if (!Directory.Exists(dir)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch
        {
            // проводник не открылся
        }
    }

    public async Task RemoveAsync(TorrentRow row)
    {
        var d = _main.Dialogs;
        if (d == null || _service == null) return;
        var ok = await d.ConfirmAsync("Убрать раздачу?",
            $"{row.Name}\n\nHaPlayer перестанет её раздавать и докачивать. Файлы на диске останутся на месте.", "Убрать");
        if (!ok) return;
        await RunAsync(() => _service.RemoveAsync(row.Manager));
    }
}
