using System.Runtime.InteropServices;

namespace HaPlayer.Ui;

/// <summary>Удаление файлов в корзину Windows: их можно вернуть оттуда, как после удаления в проводнике.</summary>
public static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOp
    {
        public IntPtr Hwnd;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        public int AnyAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref ShFileOp op);

    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x4, FofNoConfirmation = 0x10, FofAllowUndo = 0x40, FofNoErrorUi = 0x400;

    /// <summary>Переносит файл в корзину. Возвращает false, если файл занят или удалить его нельзя.</summary>
    public static bool Send(string path)
    {
        if (!File.Exists(path)) return true;
        var op = new ShFileOp
        {
            Func = FoDelete,
            From = path + '\0' + '\0',
            Flags = (ushort)(FofSilent | FofNoConfirmation | FofAllowUndo | FofNoErrorUi),
        };
        var rc = SHFileOperation(ref op);
        return rc == 0 && op.AnyAborted == 0 && !File.Exists(path);
    }
}
