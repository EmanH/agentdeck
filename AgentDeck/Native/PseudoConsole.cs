using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentDeck.Native;

/// <summary>A child process attached to a Windows pseudo console (ConPTY), the same plumbing Windows Terminal uses.</summary>
sealed class PseudoConsole : IDisposable
{
    public event Action<string>? Output;
    public event Action? Exited;

    readonly IntPtr _hpc;
    readonly FileStream _input;
    readonly FileStream _output;
    readonly IntPtr _process;
    readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    readonly object _writeGate = new();
    int _closed, _exited;

    public PseudoConsole(string commandLine, string workingDirectory, short cols, short rows)
    {
        if (!CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0) ||
            !CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        int hr = CreatePseudoConsole(new Coord(cols, rows), inRead, outWrite, 0, out _hpc);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        // ConPTY duplicated these ends; keeping ours open would stop EOF from arriving.
        inRead.Dispose();
        outWrite.Dispose();
        _input = new FileStream(inWrite, FileAccess.Write, 4096, false);
        _output = new FileStream(outRead, FileAccess.Read, 4096, false);

        var si = new StartupInfoEx();
        si.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
        si.StartupInfo.dwFlags = STARTF_USESTDHANDLES; // don't let the child inherit our std handles
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        si.lpAttributeList = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(si.lpAttributeList, 1, 0, ref size) ||
                !UpdateProcThreadAttribute(si.lpAttributeList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                                           _hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                                EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero,
                                workingDirectory, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _process = pi.hProcess;
            CloseHandle(pi.hThread);
        }
        finally
        {
            DeleteProcThreadAttributeList(si.lpAttributeList);
            Marshal.FreeHGlobal(si.lpAttributeList);
        }
    }

    /// <summary>Begin pumping output; call after subscribing to the events so nothing is missed.</summary>
    public void Start()
    {
        new Thread(ReadLoop) { IsBackground = true, Name = "pty-read" }.Start();
        new Thread(WaitLoop) { IsBackground = true, Name = "pty-wait" }.Start();
    }

    public void Write(string data)
    {
        var bytes = Encoding.UTF8.GetBytes(data);
        lock (_writeGate)
        {
            try { _input.Write(bytes); _input.Flush(); }
            catch (IOException) { } // process already gone
            catch (ObjectDisposedException) { }
        }
    }

    public void Resize(int cols, int rows)
    {
        if (Volatile.Read(ref _closed) == 0 && cols > 0 && rows > 0)
            ResizePseudoConsole(_hpc, new Coord((short)cols, (short)rows));
    }

    void ReadLoop()
    {
        var buffer = new byte[16384];
        var chars = new char[buffer.Length + 4];
        try
        {
            int n;
            while ((n = _output.Read(buffer, 0, buffer.Length)) > 0)
            {
                int c = _decoder.GetChars(buffer, 0, n, chars, 0);
                if (c > 0) Output?.Invoke(new string(chars, 0, c));
            }
        }
        catch (Exception) { }
        finally
        {
            if (Interlocked.Exchange(ref _exited, 1) == 0) Exited?.Invoke();
        }
    }

    void WaitLoop()
    {
        WaitForSingleObject(_process, INFINITE);
        ClosePty(); // output only reaches EOF once the pseudo console is closed
    }

    void ClosePty()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        ClosePseudoConsole(_hpc); // also ends every process still attached to it
    }

    public void Dispose()
    {
        ClosePty();
        lock (_writeGate) _input.Dispose();
    }

    // --- interop --------------------------------------------------------------

    const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000, CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    const int STARTF_USESTDHANDLES = 0x00000100, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    const uint INFINITE = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    readonly struct Coord(short x, short y) { readonly short X = x, Y = y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);

    [DllImport("kernel32.dll")]
    static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr hpc);

    [DllImport("kernel32.dll")]
    static extern int ResizePseudoConsole(IntPtr hpc, Coord size);

    [DllImport("kernel32.dll")]
    static extern void ClosePseudoConsole(IntPtr hpc);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value,
                                                 IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes,
                                      IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment,
                                      string? currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation info);

    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
}
