using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace HaPlayer.Ui;

/// <summary>
/// Горячие клавиши для всей системы: работают, когда окно свёрнуто или спрятано в трей.
/// RegisterHotKey с пустым окном шлёт WM_HOTKEY в очередь потока, поэтому держим свой поток с циклом сообщений.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int WM_QUIT = 0x0012;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    private const uint VK_MEDIA_NEXT = 0xB0, VK_MEDIA_PREV = 0xB1, VK_MEDIA_STOP = 0xB2, VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const uint VK_SPACE = 0x20, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;

    public enum Key { PlayPause, Next, Previous, Stop, VolumeUp, VolumeDown }

    private readonly Action<Key> _handler;
    private readonly Thread _thread;
    private uint _threadId;
    private readonly ManualResetEventSlim _ready = new();

    /// <summary>Сколько комбинаций удалось занять. Если другая программа уже держит клавишу, её пропускаем.</summary>
    public int Registered { get; private set; }

    public GlobalHotkeys(Action<Key> handler)
    {
        _handler = handler;
        _thread = new Thread(Loop) { IsBackground = true, Name = "HaPlayer hotkeys" };
        _thread.Start();
        _ready.Wait(2000);
    }

    private void Loop()
    {
        _threadId = GetCurrentThreadId();
        var map = new (uint Mods, uint Vk, Key Act)[]
        {
            (0, VK_MEDIA_PLAY_PAUSE, Key.PlayPause),
            (0, VK_MEDIA_NEXT, Key.Next),
            (0, VK_MEDIA_PREV, Key.Previous),
            (0, VK_MEDIA_STOP, Key.Stop),
            (MOD_CONTROL | MOD_ALT, VK_SPACE, Key.PlayPause),
            (MOD_CONTROL | MOD_ALT, VK_RIGHT, Key.Next),
            (MOD_CONTROL | MOD_ALT, VK_LEFT, Key.Previous),
            (MOD_CONTROL | MOD_ALT, VK_UP, Key.VolumeUp),
            (MOD_CONTROL | MOD_ALT, VK_DOWN, Key.VolumeDown),
        };
        for (var i = 0; i < map.Length; i++)
            if (RegisterHotKey(IntPtr.Zero, i + 1, map[i].Mods | MOD_NOREPEAT, map[i].Vk)) Registered++;
        _ready.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message != WM_HOTKEY) continue;
            var id = (int)msg.wParam - 1;
            if (id >= 0 && id < map.Length)
            {
                var act = map[id].Act;
                Dispatcher.UIThread.Post(() => _handler(act));
            }
        }

        for (var i = 0; i < map.Length; i++) UnregisterHotKey(IntPtr.Zero, i + 1);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public int message;
        public IntPtr wParam;
        public IntPtr lParam;
        public int time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
