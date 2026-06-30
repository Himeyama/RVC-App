using System.Runtime.InteropServices;
using System.Text;

namespace RvcRealtimeGui.Services;

/// <summary>
/// Win32 ConPTY で子プロセスをホストし、出力（生 ANSI バイト列）をコールバックで通知する。
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
    static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int    cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int    dwX;
        public int    dwY;
        public int    dwXSize;
        public int    dwYSize;
        public int    dwXCountChars;
        public int    dwYCountChars;
        public int    dwFillAttribute;
        public int    dwFlags;
        public short  wShowWindow;
        public short  cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
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
    const int  STARTF_USESHOWWINDOW         = 0x00000001;
    const short SW_HIDE                     = 0;
    static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = new IntPtr(0x00020016);

    // ── フィールド ───────────────────────────────────────────────

    IntPtr _hPC      = IntPtr.Zero;
    IntPtr _hPipeIn  = IntPtr.Zero;
    IntPtr _hPipeOut = IntPtr.Zero;
    PROCESS_INFORMATION _pi;
    CancellationTokenSource _cts = new();

    // 生 ANSI バイト列を文字列（UTF-8）として通知するコールバック
    readonly Action<string> _onOutput;

    public bool IsRunning { get; private set; }

    public PseudoConsole(Action<string> onOutput)
    {
        _onOutput = onOutput;
    }

    public void Start(string command, string? workingDirectory = null,
        short cols = 220, short rows = 50,
        Dictionary<string, string>? extraEnv = null)
    {
        if (IsRunning) return;

        // ── パイプ作成 ─────────────────────────────────────────
        if (!CreatePipe(out IntPtr hPTYInRead,  out IntPtr hPTYInWrite,  IntPtr.Zero, 0))
            throw new InvalidOperationException($"CreatePipe(in) failed: {Marshal.GetLastWin32Error()}");
        if (!CreatePipe(out IntPtr hPTYOutRead, out IntPtr hPTYOutWrite, IntPtr.Zero, 0))
            throw new InvalidOperationException($"CreatePipe(out) failed: {Marshal.GetLastWin32Error()}");

        // ── ConPTY 作成 ────────────────────────────────────────
        var size = new COORD { X = cols, Y = rows };
        int hr = CreatePseudoConsole(size, hPTYInRead, hPTYOutWrite, 0, out _hPC);
        CloseHandle(hPTYInRead);
        CloseHandle(hPTYOutWrite);
        if (hr != 0)
            throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X8}");

        _hPipeIn  = hPTYInWrite;
        _hPipeOut = hPTYOutRead;

        // ── 環境変数ブロック構築 ────────────────────────────────
        // 現在のプロセス環境に extraEnv を上書きして Unicode 環境ブロックを作る
        IntPtr envBlock = BuildEnvBlock(extraEnv);

        // ── プロセス属性リスト ──────────────────────────────────
        IntPtr attrListSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);
        IntPtr attrList = Marshal.AllocHGlobal(attrListSize);
        try
        {
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrListSize))
                throw new InvalidOperationException(
                    $"InitializeProcThreadAttributeList failed: {Marshal.GetLastWin32Error()}");

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
            si.StartupInfo.cb          = Marshal.SizeOf<STARTUPINFOEX>();
            si.StartupInfo.dwFlags     = STARTF_USESHOWWINDOW;
            si.StartupInfo.wShowWindow = SW_HIDE;
            si.lpAttributeList         = attrList;

            // cmd.exe /c を外し直接起動（PTY が子プロセスに正しく継承される）
            string fullCmd = command;

            // CREATE_UNICODE_ENVIRONMENT (0x400) を追加して Unicode 環境ブロックを使う
            const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

            if (!CreateProcess(
                    null, fullCmd,
                    IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                    envBlock, workingDirectory,
                    ref si, out _pi))
                throw new InvalidOperationException(
                    $"CreateProcess failed: {Marshal.GetLastWin32Error()}");
        }
        catch
        {
            Marshal.FreeHGlobal(attrList);
            if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
            throw;
        }
        Marshal.FreeHGlobal(attrList);
        if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);

        IsRunning = true;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => ReadLoop(ct), ct);
    }

    /// <summary>現在プロセスの環境変数に extraEnv を上書きした Unicode 環境ブロックを確保する。</summary>
    static IntPtr BuildEnvBlock(Dictionary<string, string>? extraEnv)
    {
        // 現在の環境変数を取得
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in System.Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string)(e.Value ?? "");

        if (extraEnv is not null)
            foreach (var kv in extraEnv)
                env[kv.Key] = kv.Value;

        // "KEY=VALUE\0KEY=VALUE\0\0" 形式の Unicode ブロック
        var sb = new System.Text.StringBuilder();
        foreach (var kv in env)
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        sb.Append('\0');

        string block = sb.ToString();
        IntPtr ptr = Marshal.AllocHGlobal(block.Length * 2);
        Marshal.Copy(block.ToCharArray(), 0, ptr, block.Length);
        return ptr;
    }

    /// <summary>ConPTY のサイズを変更する（WebView2 リサイズ時に呼ぶ）</summary>
    public void Resize(short cols, short rows)
    {
        if (_hPC != IntPtr.Zero)
            ResizePseudoConsole(_hPC, new COORD { X = cols, Y = rows });
    }

    void ReadLoop(CancellationToken ct)
    {
        var buf = new byte[4096];
        while (!ct.IsCancellationRequested)
        {
            bool ok = ReadFile(_hPipeOut, buf, (uint)buf.Length, out uint read, IntPtr.Zero);
            if (!ok || read == 0) break;
            // 生バイト列を UTF-8 文字列として渡す（ANSI エスケープ込み）
            string raw = Encoding.UTF8.GetString(buf, 0, (int)read);
            _onOutput(raw);
        }
        IsRunning = false;
        _onOutput("\r\n\x1b[90m[プロセス終了]\x1b[0m\r\n");
    }

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
