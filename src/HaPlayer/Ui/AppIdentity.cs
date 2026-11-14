using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using HaPlayer.Library;

namespace HaPlayer.Ui;

/// <summary>
/// Имя программы для Windows. Панель мультимедиа, группировка на панели задач и уведомления узнают программу
/// по идентификатору AppUserModelID, а название и значок берут из ярлыка в меню «Пуск» с тем же идентификатором.
/// Без ярлыка Windows пишет «Неизвестное приложение».
/// </summary>
public static class AppIdentity
{
    public const string Id = "WiWaSu.HaPlayer";

    /// <summary>Вызывается в самом начале, до первого окна.</summary>
    public static void SetForProcess()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(Id);
        }
        catch
        {
            // без идентификатора программа работает как раньше
        }
    }

    /// <summary>
    /// Ярлык HaPlayer в меню «Пуск». Создаётся, если его нет или он ведёт на другой exe (программу перенесли).
    /// В портативном режиме ничего не создаётся: портативная версия не оставляет следов в системе.
    /// </summary>
    public static void EnsureStartMenuShortcut()
    {
        if (AppPaths.Mode == DataMode.Portable) return;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "HaPlayer.lnk");
            if (File.Exists(lnk) && string.Equals(ReadTarget(lnk), exe, StringComparison.OrdinalIgnoreCase)) return;
            WriteShortcut(lnk, exe);
        }
        catch
        {
            // ярлык необязателен: без него Windows просто не покажет название
        }
    }

    /// <summary>Пишет ярлык на exe с идентификатором HaPlayer.</summary>
    internal static void WriteShortcut(string lnk, string exe)
    {
        {
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(exe);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? "");
            link.SetIconLocation(exe, 0);
            link.SetDescription("HaPlayer");

            var store = (IPropertyStore)link;
            var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
            var value = new PROPVARIANT { vt = 31 /* VT_LPWSTR */, pointer = Marshal.StringToCoTaskMemUni(Id) };
            try
            {
                store.SetValue(ref key, ref value);
                store.Commit();
            }
            finally
            {
                Marshal.FreeCoTaskMem(value.pointer);
            }
            ((IPersistFile)link).Save(lnk, true);
        }
    }

    /// <summary>Идентификатор из ярлыка (для проверки).</summary>
    internal static string? ReadId(string lnk)
    {
        var link = (IShellLinkW)new ShellLink();
        ((IPersistFile)link).Load(lnk, 0);
        var store = (IPropertyStore)link;
        var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
        store.GetValue(ref key, out var value);
        return value.vt == 31 ? Marshal.PtrToStringUni(value.pointer) : null;
    }

    internal static string? ReadTarget(string lnk)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(lnk, 0);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(string appId);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046"), ClassInterface(ClassInterfaceType.None)]
    private class ShellLink;

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idl);
        void SetIDList(IntPtr idl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PROPERTYKEY key);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
    }
}
