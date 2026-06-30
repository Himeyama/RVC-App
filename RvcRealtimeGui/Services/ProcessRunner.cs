using System.Diagnostics;
using System.Text;

namespace RvcRealtimeGui.Services;

/// <summary>
/// 子プロセスを起動し、stdout / stderr を ANSI 文字列としてコールバックで通知する。
/// ConPTY ではなく System.Diagnostics.Process のリダイレクトを使うシンプルな実装。
/// </summary>
public sealed class ProcessRunner : IDisposable
{
    readonly Action<string> _onOutput;
    Process? _proc;

    public bool IsRunning => _proc is { HasExited: false };
    public int? Pid => _proc?.Id;

    public ProcessRunner(Action<string> onOutput)
    {
        _onOutput = onOutput;
    }

    public void Start(string fileName, string arguments,
        string? workingDirectory = null,
        Dictionary<string, string>? extraEnv = null)
    {
        if (IsRunning) return;

        var psi = new ProcessStartInfo
        {
            FileName               = fileName,
            Arguments              = arguments,
            WorkingDirectory       = workingDirectory ?? "",
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };

        if (extraEnv is not null)
            foreach (var kv in extraEnv)
                psi.Environment[kv.Key] = kv.Value;

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        _proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) _onOutput(e.Data + "\r\n");
        };
        _proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) _onOutput(e.Data + "\r\n");
        };
        _proc.Exited += (_, _) =>
        {
            _onOutput($"\r\n\x1b[90m[プロセス終了 exit={_proc?.ExitCode}]\x1b[0m\r\n");
        };

        if (!_proc.Start())
            throw new InvalidOperationException("Process.Start failed");

        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
    }

    /// <summary>
    /// プロセスツリーを強制終了する。
    /// </summary>
    public void Stop()
    {
        if (_proc is null) return;
        try
        {
            if (!_proc.HasExited)
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(2000);
            }
        }
        catch { }
        try { _proc.Dispose(); } catch { }
        _proc = null;
    }

    public void Dispose() => Stop();
}
