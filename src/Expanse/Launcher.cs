using System.Runtime.InteropServices;
using Expanse.Native;
using Microsoft.Win32;

namespace Expanse;

/// Finds and starts what a group's command line names.
static class Launcher
{
    const uint SEE_MASK_FLAG_NO_UI = 0x400;

    /// `File`: what's opened (a path, or as typed where none was found). `Program`: the program that
    /// runs, if it's known. `Found`: `File` is a file or folder that exists.
    public sealed record Target(string File, string Arguments, string? Program, bool Found);

    static readonly string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// Looks on disk and in the registry, so not on the UI thread: a dead network drive on PATH is slow.
    public static Target Find(string line)
    {
        var (typed, arguments) = GroupCommands.Split(Environment.ExpandEnvironmentVariables(line), p => File.Exists(p) || Directory.Exists(p));
        if (Locate(typed) is not string path) return new Target(typed, arguments, null, false);
        if (Directory.Exists(path)) return new Target(path, arguments, Path.Combine(windows, "explorer.exe"), true);
        path = AppOfScript(path) ?? path;
        return new Target(path, arguments, IsProgram(path) ? path : Processes.Opening(path), true);
    }

    static bool IsProgram(string path) => Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".com" or ".cmd" or ".bat";

    /// Whether its window can be Windows Terminal's. The Store's programs that can't be read count as
    /// not (`wt` is told by its name).
    public static bool OpensTerminal(string program)
    {
        var start = new byte[4096];
        int read = 0;
        try
        {
            using var file = new FileStream(program, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            read = file.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        return GroupCommands.OpensTerminal(program, start.AsSpan(0, read));
    }

    /// As typed, then on PATH, then among the App Paths, as the Run box finds it.
    static string? Locate(string typed)
    {
        try
        {
            if (typed.Length == 0) return null;
            var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);
            string? At(string path)
            {
                if (Path.HasExtension(path) && File.Exists(path)) return path;
                foreach (var e in extensions) if (File.Exists(path + e)) return path + e;
                return File.Exists(path) ? path : null;
            }
            if (typed.Contains('\\') || typed.Contains('/'))
                return !Path.IsPathFullyQualified(typed) ? null : At(typed) ?? (Directory.Exists(typed) ? Path.GetFullPath(typed) : null);
            foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (At(Path.Combine(folder.Trim('"'), typed)) is string found) return found;
            string key = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{(Path.HasExtension(typed) ? typed : typed + ".exe")}";
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var app = root.OpenSubKey(key);
                if (app?.GetValue(null) is string path && path.Trim('"') is var plain && File.Exists(plain)) return plain;
            }
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return null;
    }

    /// `bin\code.cmd` starts the Code.exe above it, and would show a console as it did. The exe takes
    /// the same arguments, and has the icon.
    static string? AppOfScript(string path)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is not (".cmd" or ".bat")) return null;
        string? folder = Path.GetDirectoryName(path), above = Path.GetDirectoryName(folder);
        if (above == null || !Path.GetFileName(folder)!.Equals("bin", StringComparison.OrdinalIgnoreCase)) return null;
        string exe = Path.Combine(above, Path.GetFileNameWithoutExtension(path) + ".exe");
        return File.Exists(exe) ? exe : null;
    }

    /// Opens it as a shortcut would, in your own folder (Expanse's would be locked by it). As you,
    /// even when Expanse is administrator: a command is never elevated because Expanse is. Its process
    /// (0: none of its own, or not known), or Windows' error. On a thread of its own: it can wait on
    /// the app, or on you.
    public static (uint pid, int error) Start(Target t)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Elevation.IsElevated)
        {
            if (!t.Found && !t.File.Contains(':')) return (0, 2);
            string line = t.Found && IsProgram(t.File) ? $"\"{t.File}\" {t.Arguments}" : $"\"{Path.Combine(windows, "explorer.exe")}\" \"{t.File}\"";
            int refused = Processes.StartAsDesktopUser(line, home, out uint started);
            return (refused == 0 ? started : 0, refused);
        }
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = Shell32.SEE_MASK_NOCLOSEPROCESS | Shell32.SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI,
            lpFile = t.File, lpParameters = t.Arguments, lpDirectory = home,
            nShow = User32.SW_SHOWNORMAL,
        };
        if (!Shell32.ShellExecuteEx(ref info)) return (0, Marshal.GetLastPInvokeError() is int error && error != 0 ? error : 2);
        if (info.hProcess == 0) return (0, 0);
        uint pid = Processes.GetProcessId(info.hProcess);
        Kernel32.CloseHandle(info.hProcess);
        return (pid, 0);
    }
}
