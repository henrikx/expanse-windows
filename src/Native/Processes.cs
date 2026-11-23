using System.Runtime.InteropServices;
using System.Text;

namespace Expanse.Native;

/// Starting programs, and which process started which.
public static class Processes
{
    const uint TH32CS_SNAPPROCESS = 2, TOKEN_ASSIGN_PRIMARY = 1, TOKEN_DUPLICATE = 2, TOKEN_QUERY = 8, TOKEN_ADJUST_DEFAULT = 0x80, TOKEN_ADJUST_SESSIONID = 0x100;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32
    {
        public int dwSize;
        public uint cntUsage, th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    struct PROCESS_INFORMATION
    {
        public nint hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] static extern bool Process32First(nint snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] static extern bool Process32Next(nint snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll")] public static extern uint GetProcessId(nint process);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindExecutableW")] static extern nint FindExecutable(string file, string? folder, StringBuilder result);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DuplicateTokenEx(nint token, uint access, nint attributes, int level, int type, out nint made);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessWithTokenW(nint token, uint logonFlags, string? program, StringBuilder commandLine, uint flags, nint environment, string? folder,
        ref STARTUPINFO startup, out PROCESS_INFORMATION made);

    /// The processes that led to `pid`: its parent, its parent's parent, and so on.
    public static List<uint> Ancestors(uint pid)
    {
        var parents = new Dictionary<uint, uint>();
        nint snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == -1) return new();
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = Marshal.SizeOf<PROCESSENTRY32>() };
            for (bool more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
                parents[entry.th32ProcessID] = entry.th32ParentProcessID;
        }
        finally { Kernel32.CloseHandle(snapshot); }
        var found = new List<uint>();
        while (parents.TryGetValue(pid, out uint parent) && parent != 0 && !found.Contains(parent) && found.Count < 32)
        {
            found.Add(parent);
            pid = parent;
        }
        return found;
    }

    /// The program that opens a document, if any.
    public static string? Opening(string file)
    {
        var sb = new StringBuilder(1024);
        return FindExecutable(file, null, sb) > 32 && sb.Length > 0 ? sb.ToString() : null;
    }

    /// Starts a command line as the desktop's user, not administrator, from a process that is one:
    /// with Explorer's own token. 0 if it started, with its process id; otherwise Windows' error.
    public static int StartAsDesktopUser(string commandLine, string folder, out uint pid)
    {
        pid = 0;
        User32.GetWindowThreadProcessId(User32.GetShellWindow(), out uint shell);
        nint process = shell == 0 ? 0 : Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, shell);
        if (process == 0) return 5;
        nint token = 0, primary = 0;
        try
        {
            if (!Advapi32.OpenProcessToken(process, TOKEN_DUPLICATE, out token)) return Marshal.GetLastPInvokeError();
            if (!DuplicateTokenEx(token, TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID, 0, 2, 1, out primary))
                return Marshal.GetLastPInvokeError();
            var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            if (!CreateProcessWithTokenW(primary, 0, null, new StringBuilder(commandLine, commandLine.Length + 1), 0, 0, folder, ref startup, out var made))
                return Marshal.GetLastPInvokeError();
            pid = made.dwProcessId;
            Kernel32.CloseHandle(made.hThread);
            Kernel32.CloseHandle(made.hProcess);
            return 0;
        }
        finally
        {
            if (primary != 0) Kernel32.CloseHandle(primary);
            if (token != 0) Kernel32.CloseHandle(token);
            Kernel32.CloseHandle(process);
        }
    }
}
