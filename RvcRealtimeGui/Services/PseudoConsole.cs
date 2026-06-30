using Microsoft.UI.Dispatching;
using System.Runtime.InteropServices;
using System.Text;

namespace RvcRealtimeGui.Services;

/// <summary>
/// Win32 ConPTY で子プロセスをホストし、出力をコールバックで通知する。
/// </summary>
public sealed class PseudoConsole : IDisposable
{
    // ── P/Invoke ────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CreatePipe(out IntPtr hRead, out IntPtr hWrite,
        IntPtr lpSecurityAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int CreatePseudoConsole(COORD size,
        IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute,
        ref IntPtr lpValue, IntPtr cbSize,
        IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(
        string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory,
        [In] ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    // ── 構造体 ──────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    struct COORD { public short X; public short Y; }

    // x64: cb(4) + pad(4) + 9×IntPtr(72) + 4×int(16) + 2×short(4) + pad(4) = 104 bytes
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int    cb;           // 4
        public IntPtr lpReserved;   // 8
        public IntPtr lpDesktop;    // 8
        public IntPtr lpTitle;      // 8
        public int    dwX;          // 4
        public int    dwY;          // 4
        public int    dwXSize;      // 4
        public int    dwYSize;      // 4
        public int    dwXCountChars;// 4
        public int    dwYCountChars;// 4
        public int    dwFillAttribute; // 4
        public int    dwFlags;      // 4
        public short  wShowWindow;  // 2
        public short  cbReserved2;  // 2
        public IntPtr lpReserved2;  // 8
        public IntPtr hStdInput;    // 8
        public IntPtr hStdOutput;   // 8
        public IntPtr hStdError;    // 8
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr      lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int    dwProcessId;
        public int    dwThreadId;
    }

    const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    // PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = ProcThreadAttributeValue(22, false, true, false)
    static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = new IntPtr(0x00020016);

    // ── フィールド ───────────────────────────────────────────────

    IntPtr _hPC      = IntPtr.Zero;
    IntPtr _hPipeIn  = IntPtr.Zero;
    IntPtr _hPipeOut = IntPtr.Zero;
    PROCESS_INFORMATION _pi;
    CancellationTokenSource _cts = new();
    readonly Action<string> _onOutput;
    readonly DispatcherQueue _dispatcher;

    public bool IsRunning { get; private set; }

    public PseudoConsole(Action<string> onOutput, DispatcherQueue dispatcher)
    {
        _onOutput   = onOutput;
        _dispatcher = dispatcher;
    }

    public void Start(string command, string? workingDirectory = null,
        short cols = 200, short rows = 50)
    {
        if (IsRunning) return;

        // ── パイプ作成 ─────────────────────────────────────────
        if (!CreatePipe(out IntPtr hPTYInRead,  out IntPtr hPTYInWrite,  IntPtr.Zero, 0))
            throw new InvalidOperationException($"CreatePipe(in) failed: {Marshal.GetLastWin32Error()}");
        if (!CreatePipe(out IntPtr hPTYOutRead, out IntPtr hPTYOutWrite, IntPtr.Zero, 0))
            throw new InvalidOperationException($"CreatePipe(out) failed: {Marshal.GetLastWin32Error()}");

        // ── ConPTY 作成 ────────────────────────────────────────
        // hPTYInRead  = PTY の stdin  (子プロセスが読む)
        // hPTYOutWrite= PTY の stdout (子プロセスが書く)
        var size = new COORD { X = cols, Y = rows };
        int hr = CreatePseudoConsole(size, hPTYInRead, hPTYOutWrite, 0, out _hPC);
        // ConPTY が内部でコピーするので子側のハンドルは閉じる
        CloseHandle(hPTYInRead);
        CloseHandle(hPTYOutWrite);
        if (hr != 0)
            throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X8}");

        // ホストが使うハンドル
        _hPipeIn  = hPTYInWrite;  // ホスト → 子に送信
        _hPipeOut = hPTYOutRead;  // 子 → ホストから受信

        // ── プロセス属性リスト ──────────────────────────────────
        IntPtr attrListSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);
        IntPtr attrList = Marshal.AllocHGlobal(attrListSize);
        try
        {
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrListSize))
                throw new InvalidOperationException(
                    $"InitializeProcThreadAttributeList failed: {Marshal.GetLastWin32Error()}");

            // lpValue に hPC の値そのものをポインタとして渡す
            IntPtr hPCVal = _hPC;
            if (!UpdateProcThreadAttribute(
                    attrList, 0,
                    PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    ref hPCVal,
                    new IntPtr(IntPtr.Size),
                    IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException(
                    $"UpdateProcThreadAttribute failed: {Marshal.GetLastWin32Error()}");

            var si = new STARTUPINFOEX();
            // cb には STARTUPINFOEX 全体のサイズを渡す (ConPTY の要件)
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.lpAttributeList = attrList;

            // cmd /c でラップして PATH・環境変数を継承
            string fullCmd = $"cmd.exe /c {command}";

            if (!CreateProcess(
                    null, fullCmd,
                    IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT,
                    IntPtr.Zero, workingDirectory,
                    ref si, out _pi))
                throw new InvalidOperationException(
                    $"CreateProcess failed: {Marshal.GetLastWin32Error()}");
        }
        catch
        {
            Marshal.FreeHGlobal(attrList);
            throw;
        }
        // attrList はプロセス起動後に解放しても OK
        Marshal.FreeHGlobal(attrList);

        IsRunning = true;

        // ── 読み取りループ ──────────────────────────────────────
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => ReadLoop(ct), ct);
    }

    void ReadLoop(CancellationToken ct)
    {
        var buf = new byte[4096];
        while (!ct.IsCancellationRequested)
        {
            bool ok = ReadFile(_hPipeOut, buf, (uint)buf.Length, out uint read, IntPtr.Zero);
            if (!ok || read == 0) break;
            string raw   = Encoding.UTF8.GetString(buf, 0, (int)read);
            string plain = StripAnsi(raw);
            if (plain.Length > 0)
                _dispatcher.TryEnqueue(() => _onOutput(plain));
        }
        IsRunning = false;
        _dispatcher.TryEnqueue(() => _onOutput("\n[プロセス終了]\n"));
    }

    static readonly System.Text.RegularExpressions.Regex _ansiRe =
        new(@"\x1B(\[[0-9;?]*[A-Za-z]|[()][AB012]|\].*?(?:\x07|\x1B\\))",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    static string StripAnsi(string s) => _ansiRe.Replace(s, "");

    public void Stop()
    {
        if (!IsRunning && _pi.hProcess == IntPtr.Zero) return;
        IsRunning = false;
        _cts.Cancel();

        if (_pi.hProcess != IntPtr.Zero)
        {
            TerminateProcess(_pi.hProcess, 0);
            CloseHandle(_pi.hProcess);
            CloseHandle(_pi.hThread);
            _pi = default;
        }
        if (_hPC      != IntPtr.Zero) { ClosePseudoConsole(_hPC); _hPC      = IntPtr.Zero; }
        if (_hPipeOut != IntPtr.Zero) { CloseHandle(_hPipeOut);   _hPipeOut = IntPtr.Zero; }
        if (_hPipeIn  != IntPtr.Zero) { CloseHandle(_hPipeIn);    _hPipeIn  = IntPtr.Zero; }
    }

    public void Dispose() => Stop();
}
