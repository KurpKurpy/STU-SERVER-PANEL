using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ServerAgent.Steam;

// A child process attached to a Windows pseudo console (ConPTY). SteamCMD buffers its output when stdout is a pipe, so prompts
// and progress never arrive (Phase 5a spike S2); inside a pseudo console it writes as it would to a real console window.
public sealed class PseudoConsoleProcess : IDisposable
{
    private readonly IntPtr _console;
    private readonly IntPtr _attributes;
    private readonly IntPtr _process;
    private readonly IntPtr _thread;
    private int _closed;

    public int Id { get; }
    public Stream Output { get; }
    public Stream Input { get; }
    public Task<int> Exited { get; }

    private PseudoConsoleProcess(IntPtr console, IntPtr attributes, Native.PROCESS_INFORMATION pi, Stream output, Stream input)
    {
        (_console, _attributes, _process, _thread, Id, Output, Input) = (console, attributes, pi.hProcess, pi.hThread, pi.dwProcessId, output, input);
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(_process, ownsHandle: false) };
        ThreadPool.RegisterWaitForSingleObject(wait, (_, _) =>
        {
            Native.GetExitCodeProcess(_process, out var code);
            done.TrySetResult(code);
            wait.Dispose();
        }, null, Timeout.Infinite, executeOnlyOnce: true);
        Exited = done.Task;
    }

    public static PseudoConsoleProcess Start(string exe, IReadOnlyList<string> args, string? workingDirectory)
    {
        if (!Native.CreatePipe(out var consoleIn, out var inputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!Native.CreatePipe(out var outputRead, out var consoleOut, IntPtr.Zero, 0)) throw new Win32Exception();
        // Wide enough that SteamCMD's long result lines never wrap.
        var hr = Native.CreatePseudoConsole(new Native.COORD { X = 1000, Y = 50 }, consoleIn, consoleOut, 0, out var console);
        consoleIn.Dispose();
        consoleOut.Dispose();
        if (hr != 0) throw new Win32Exception(hr);

        var size = IntPtr.Zero;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attributes = Marshal.AllocHGlobal(size);
        if (!Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref size)
            || !Native.UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x00020016 /* PSEUDOCONSOLE */, console, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception();

        var si = new Native.STARTUPINFOEX { lpAttributeList = attributes };
        si.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
        si.StartupInfo.dwFlags = 0x00000100; // STARTF_USESTDHANDLES with none given: the child uses the pseudo console, not our stdio
        var commandLine = new StringBuilder(string.Join(' ', new[] { exe }.Concat(args).Select(Quote)));
        if (!Native.CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0x00080000 /* EXTENDED_STARTUPINFO_PRESENT */,
                IntPtr.Zero, workingDirectory, ref si, out var pi))
        {
            var error = new Win32Exception();
            Native.ClosePseudoConsole(console);
            Native.DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            throw error;
        }
        return new PseudoConsoleProcess(console, attributes, pi, new FileStream(outputRead, FileAccess.Read), new FileStream(inputWrite, FileAccess.Write));
    }

    public void Kill()
    {
        try { using var p = Process.GetProcessById(Id); p.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { Native.TerminateProcess(_process, 1); }
    }

    // Closing the console ends the output stream once the reader has drained it; call it after the process exits.
    public void CloseConsole()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) Native.ClosePseudoConsole(_console);
    }

    public void Dispose()
    {
        CloseConsole();
        Input.Dispose();
        Output.Dispose();
        Native.DeleteProcThreadAttributeList(_attributes);
        Marshal.FreeHGlobal(_attributes);
        Native.CloseHandle(_thread);
        Native.CloseHandle(_process);
    }

    // CommandLineToArgvW rules: quote when needed, and double the backslashes that precede a quote.
    private static string Quote(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => c is ' ' or '\t' or '"')) return arg;
        var sb = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            sb.Append(c);
        }
        return sb.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct COORD { public short X, Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb; public string? lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)] public struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
        [StructLayout(LayoutKind.Sequential)] public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);
        [DllImport("kernel32.dll")]
        public static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
        [DllImport("kernel32.dll")]
        public static extern void ClosePseudoConsole(IntPtr console);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
        [DllImport("kernel32.dll")]
        public static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessW(string? app, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inherit,
            uint flags, IntPtr environment, string? directory, ref STARTUPINFOEX startupInfo, out PROCESS_INFORMATION info);
        [DllImport("kernel32.dll")]
        public static extern bool GetExitCodeProcess(IntPtr process, out int code);
        [DllImport("kernel32.dll")]
        public static extern bool TerminateProcess(IntPtr process, int code);
        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
