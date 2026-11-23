using System.Runtime.InteropServices;

namespace TinyClips.App.Services.Files;

internal static class ShellRecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    public static bool MoveFile(string path)
    {
        var operation = new ShFileOpStruct
        {
            wFunc = FoDelete,
            pFrom = path + "\0\0",
            fFlags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi),
        };
        var result = SHFileOperation(ref operation);
        return result == 0 && !operation.fAnyOperationsAborted && !File.Exists(path);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public nint hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public nint hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct lpFileOp);
}
