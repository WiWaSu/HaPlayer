using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HaPlayer.ViewModels;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace HaPlayer.Ui;

/// <summary>
/// Встраивание в Windows: трек с обложкой в системной панели мультимедиа (возле громкости и на экране блокировки),
/// кнопки «назад, играть, вперёд» в миниатюре окна на панели задач и прогресс трека на значке в панели задач.
/// Если что-то из этого Windows не поддерживает, плеер работает как раньше.
/// </summary>
public sealed class WindowsMedia : IDisposable
{
    private readonly Window _window;
    private readonly MainViewModel _vm;
    private MediaPlayer? _mediaPlayer;
    private SystemMediaTransportControls? _smtc;
    private ITaskbarList3? _taskbar;
    private IntPtr _hwnd;
    private readonly uint _taskbarCreatedMsg = RegisterWindowMessage("TaskbarButtonCreated");
    private IntPtr _iconPrev, _iconPlay, _iconPause, _iconNext;
    private bool _buttonsAdded;
    private TrackItem? _shownTrack;
    private int _coverSlot;
    private TaskbarProgress _lastState = (TaskbarProgress)(-1);
    private int _lastPercent = -1;

    private const int IdPrev = 1, IdPlay = 2, IdNext = 3;

    public WindowsMedia(Window window, MainViewModel vm)
    {
        _window = window;
        _vm = vm;
        InitMediaControls();
        window.Opened += (_, _) =>
        {
            _hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            Win32Properties.AddWndProcHookCallback(window, WndProc);
        };
        vm.Player.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(PlayerViewModel.CurrentTrack):
                case nameof(PlayerViewModel.IsPlaying):
                    UpdateMediaControls();
                    UpdateThumbButtons();
                    UpdateProgress();
                    break;
                case nameof(PlayerViewModel.Position):
                    UpdateProgress();
                    break;
            }
        };
    }

    // ---------- Системная панель мультимедиа ----------

    private void InitMediaControls()
    {
        try
        {
            // Классическому окну SMTC проще всего получить через MediaPlayer, который сам ничего не играет.
            _mediaPlayer = new MediaPlayer();
            _mediaPlayer.CommandManager.IsEnabled = false;
            _smtc = _mediaPlayer.SystemMediaTransportControls;
            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsNextEnabled = true;
            _smtc.IsPreviousEnabled = true;
            _smtc.IsStopEnabled = true;
            _smtc.ButtonPressed += (_, e) => Dispatcher.UIThread.Post(() => OnButton(e.Button));
        }
        catch
        {
            // Старая Windows или служба недоступна: обходимся без панели.
            _smtc = null;
        }
    }

    private void OnButton(SystemMediaTransportControlsButton button)
    {
        var p = _vm.Player;
        switch (button)
        {
            case SystemMediaTransportControlsButton.Play: p.Play(); break;
            case SystemMediaTransportControlsButton.Pause: p.Pause(); break;
            case SystemMediaTransportControlsButton.Next: p.Next(); break;
            case SystemMediaTransportControlsButton.Previous: p.Previous(); break;
            case SystemMediaTransportControlsButton.Stop: p.Stop(); break;
        }
    }

    private async void UpdateMediaControls()
    {
        if (_smtc == null) return;
        try
        {
            var track = _vm.Player.CurrentTrack;
            _smtc.PlaybackStatus = track == null ? MediaPlaybackStatus.Closed
                : _vm.Player.IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
            if (track == _shownTrack) return;
            _shownTrack = track;

            var updater = _smtc.DisplayUpdater;
            updater.ClearAll();
            if (track == null)
            {
                updater.Update();
                return;
            }
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = track.Title;
            updater.MusicProperties.Artist = track.Artist;
            updater.MusicProperties.AlbumTitle = track.AlbumName;
            updater.Update();

            // Обложка грузится дольше, поэтому приходит вторым обновлением. Та же, что в нижней панели:
            // обложка альбома или своё фото, иначе встроенная в файл.
            var source = track.Album?.CoverSource ?? track.Path;
            if (await _vm.Covers.GetAsync(source, 300) is { } bitmap && _shownTrack == track)
            {
                // Windows надёжнее всего берёт картинку из файла. Два имени по очереди: иначе панель
                // может показать закэшированную старую обложку под тем же именем.
                var dir = Path.Combine(Path.GetTempPath(), "HaPlayer");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"cover-{_coverSlot ^= 1}.png");
                await Task.Run(() =>
                {
                    using var fs = File.Create(file);
                    bitmap.Save(fs);
                });
                if (_shownTrack != track) return;
                var storage = await Windows.Storage.StorageFile.GetFileFromPathAsync(file);
                updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(storage);
                updater.Update();
            }
        }
        catch
        {
            // панель мультимедиа необязательна
        }
    }

    // ---------- Панель задач: кнопки в миниатюре и прогресс ----------

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMsg)
        {
            // Explorer создал (или пересоздал после перезапуска) кнопку окна: кнопки миниатюры добавляются заново.
            _hwnd = hwnd;
            _buttonsAdded = false;
            InitTaskbar();
        }
        else if (msg == WM_COMMAND && ((long)wParam >> 16 & 0xFFFF) == THBN_CLICKED)
        {
            var id = (int)((long)wParam & 0xFFFF);
            var p = _vm.Player;
            if (id == IdPrev) p.Previous();
            else if (id == IdPlay) p.PlayPause();
            else if (id == IdNext) p.Next();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void InitTaskbar()
    {
        try
        {
            _taskbar ??= (ITaskbarList3)new TaskbarListCom();
            _taskbar.HrInit();
            if (_iconPrev == IntPtr.Zero)
            {
                _iconPrev = MakeIcon(Icons.Prev);
                _iconPlay = MakeIcon(Icons.Play);
                _iconPause = MakeIcon(Icons.Pause);
                _iconNext = MakeIcon(Icons.Next);
            }
            var buttons = Buttons();
            _taskbar.ThumbBarAddButtons(_hwnd, (uint)buttons.Length, buttons);
            _buttonsAdded = true;
            _lastState = (TaskbarProgress)(-1);
            UpdateProgress();
        }
        catch
        {
            _taskbar = null;
        }
    }

    private THUMBBUTTON[] Buttons()
    {
        var playing = _vm.Player.IsPlaying;
        var enabled = _vm.Player.HasTrack ? THBF.ENABLED : THBF.DISABLED;
        return
        [
            Button(IdPrev, _iconPrev, L.T("Предыдущий"), enabled),
            Button(IdPlay, playing ? _iconPause : _iconPlay, playing ? L.T("Пауза") : L.T("Играть"), enabled),
            Button(IdNext, _iconNext, L.T("Следующий"), enabled),
        ];
    }

    private static THUMBBUTTON Button(int id, IntPtr icon, string tip, THBF flags) => new()
    {
        dwMask = THB.ICON | THB.TOOLTIP | THB.FLAGS,
        iId = (uint)id,
        hIcon = icon,
        szTip = tip,
        dwFlags = flags,
    };

    private void UpdateThumbButtons()
    {
        if (_taskbar == null || !_buttonsAdded) return;
        try
        {
            var buttons = Buttons();
            _taskbar.ThumbBarUpdateButtons(_hwnd, (uint)buttons.Length, buttons);
        }
        catch
        {
            // кнопки необязательны
        }
    }

    private void UpdateProgress()
    {
        if (_taskbar == null || _hwnd == IntPtr.Zero) return;
        try
        {
            var p = _vm.Player;
            var state = !p.HasTrack || p.Duration <= 0 ? TaskbarProgress.NoProgress
                : p.IsPlaying ? TaskbarProgress.Normal : TaskbarProgress.Paused;
            if (state != _lastState)
            {
                _taskbar.SetProgressState(_hwnd, state);
                _lastState = state;
            }
            if (state == TaskbarProgress.NoProgress) return;
            var percent = (int)(p.Position / p.Duration * 1000);
            if (percent == _lastPercent) return;
            _lastPercent = percent;
            _taskbar.SetProgressValue(_hwnd, (ulong)Math.Clamp(percent, 0, 1000), 1000);
        }
        catch
        {
            // прогресс необязателен
        }
    }

    /// <summary>Значок кнопки из того же векторного значка, что в плеере: белый, 32×32.</summary>
    private static IntPtr MakeIcon(Geometry geometry)
    {
        const int size = 32;
        var glyph = new Glyph
        {
            Data = geometry,
            Width = 22,
            Height = 22,
            Foreground = Brushes.White,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var host = new Border { Width = size, Height = size, Child = glyph };
        host.Measure(new Size(size, size));
        host.Arrange(new Rect(0, 0, size, size));
        using var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        rtb.Render(host);

        var pixels = new byte[size * size * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            rtb.CopyPixels(new PixelRect(0, 0, size, size), handle.AddrOfPinnedObject(), pixels.Length, size * 4);
        }
        finally
        {
            handle.Free();
        }

        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size, // сверху вниз
            biPlanes = 1,
            biBitCount = 32,
        };
        var color = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        var mask = CreateBitmap(size, size, 1, 1, new byte[size * size / 8]);
        var info = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
        var icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    public void Dispose()
    {
        try
        {
            if (_smtc != null) _smtc.IsEnabled = false;
            _mediaPlayer?.Dispose();
        }
        catch
        {
            // выходим
        }
        foreach (var icon in new[] { _iconPrev, _iconPlay, _iconPause, _iconNext })
            if (icon != IntPtr.Zero) DestroyIcon(icon);
    }

    // ---------- Win32 ----------

    private const uint WM_COMMAND = 0x0111;
    private const int THBN_CLICKED = 0x1800;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, byte[] bits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [Flags]
    private enum THB : uint { BITMAP = 1, ICON = 2, TOOLTIP = 4, FLAGS = 8 }

    [Flags]
    private enum THBF : uint { ENABLED = 0, DISABLED = 1, DISMISSONCLICK = 2, NOBACKGROUND = 4, HIDDEN = 8 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct THUMBBUTTON
    {
        public THB dwMask;
        public uint iId;
        public uint iBitmap;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szTip;
        public THBF dwFlags;
    }

    private enum TaskbarProgress { NoProgress = 0, Indeterminate = 1, Normal = 2, Error = 4, Paused = 8 }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarListCom;

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        // ITaskbarList3
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, TaskbarProgress state);
        void RegisterTab(IntPtr hwndTab, IntPtr hwndMdi);
        void UnregisterTab(IntPtr hwndTab);
        void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        void SetTabActive(IntPtr hwndTab, IntPtr hwndMdi, uint reserved);
        void ThumbBarAddButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
        void ThumbBarUpdateButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
        void ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);
        void SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string description);
        void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string tip);
        void SetThumbnailClip(IntPtr hwnd, IntPtr clip);
    }
}
