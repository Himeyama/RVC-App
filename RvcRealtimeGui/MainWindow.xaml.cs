using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RvcRealtimeGui.Pages;
using RvcRealtimeGui.Services;

namespace RvcRealtimeGui;

public sealed partial class MainWindow : Window
{
    const int NormalPollIntervalMs = 3000;
    const int FastPollIntervalMs = 500;
    const int FastPollDurationMs = 10000;

    readonly RvcApiClient _api = new();
    readonly DispatcherQueue _dispatcher;
    readonly SemaphoreSlim _statusPollTrigger = new(0);
    CancellationTokenSource? _statusCts;
    long _fastPollUntilTick;

    ProcessRunner? _runner;

    RvcModePage? _rvcModePage;
    ModelTuningPage? _modelTuningPage;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarGrid);
        try { AppWindow.SetIcon("Assets/AppIcon.ico"); } catch { }

        _dispatcher = DispatcherQueue.GetForCurrentThread();

        Activated += async (_, _) => await InitializeAsync();

        // Closed は async を await できないため AppWindow.Closing で代替する
        AppWindow.Closing += OnWindowClosing;

        RootNav.SelectedItem = RootNav.MenuItems[0];
        NavigateTo("rvc");
    }

    // ── ナビゲーション ──────────────────────────────────────────

    void RootNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem item && item.Tag is string tag)
            NavigateTo(tag);
    }

    void NavigateTo(string tag)
    {
        switch (tag)
        {
            case "tuning":
                if (_modelTuningPage is null)
                {
                    _modelTuningPage = new ModelTuningPage();
                    _modelTuningPage.Initialize(new ModelTuningPageContext { Api = _api });
                }
                ContentFrame.Content = _modelTuningPage;
                break;
            default:
                if (_rvcModePage is null)
                {
                    _rvcModePage = new RvcModePage();
                    _rvcModePage.Initialize(_api);
                    _rvcModePage.ToggleServerRequested += async (_, _) => await ToggleServerAsync();
                    _rvcModePage.SetServerRunning(_runner?.IsRunning == true);
                }
                ContentFrame.Content = _rvcModePage;
                break;
        }
    }

    // ── 初期化 ──────────────────────────────────────────────────

    async Task InitializeAsync()
    {
        if (_statusCts is not null) return;
        _statusCts = new CancellationTokenSource();
        _ = PollServerStatusAsync(_statusCts.Token);
        await Task.CompletedTask;
    }

    // ── サーバープロセス管理 ────────────────────────────────────

    void SetServerRunning(bool running) => _rvcModePage?.SetServerRunning(running);

    void StartServerProcess()
    {
        _runner?.Dispose();
        _runner = new ProcessRunner(text => _dispatcher.TryEnqueue(() => AppendTerminal(text)));

        string projectRoot = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\.."));
        string apiScript = System.IO.Path.Combine(projectRoot, "api_gui.py");

        string uvExe = FindInPath("uv.exe") ?? "uv.exe";
        string args = $"run python -u \"{apiScript}\"";

        var env = new Dictionary<string, string>
        {
            ["PYTHONUNBUFFERED"] = "1",
            ["PYTHONIOENCODING"] = "utf-8",
            ["FORCE_COLOR"] = "1",
        };

        try
        {
            _runner.Start(uvExe, args, workingDirectory: projectRoot, extraEnv: env);
            AppendTerminal($"\x1b[90m[RvcRealtimeGui] サーバー起動: {uvExe} {args} (PID={_runner.Pid})\x1b[0m\r\n");
            SetServerRunning(true);
            _rvcModePage?.SetVcSpinner(true);
        }
        catch (Exception ex)
        {
            AppendTerminal($"\x1b[31m[RvcRealtimeGui] 起動失敗: {ex.Message}\x1b[0m\r\n");
            _runner?.Dispose();
            _runner = null;
        }
    }

    void AppendTerminal(string text) => _rvcModePage?.AppendTerminal(text);

    static string? FindInPath(string exe)
    {
        string pathVar = System.Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in pathVar.Split(';'))
        {
            string full = System.IO.Path.Combine(dir.Trim(), exe);
            if (System.IO.File.Exists(full)) return full;
        }
        return null;
    }

    async void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender,
                               Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        // 一旦閉じるをキャンセルしてクリーンアップを待つ
        args.Cancel = true;
        _statusCts?.Cancel();
        await StopServerProcessAsync();
        _api.Dispose();
        // クリーンアップ完了後にウィンドウを破棄
        AppWindow.Closing -= OnWindowClosing;
        AppWindow.Destroy();
    }

    async Task StopServerProcessAsync()
    {
        if (_rvcModePage?.IsVcRunning == true)
        {
            await _rvcModePage.StopVcIfRunningAsync();
        }

        // 子プロセス (uv.exe → python.exe) をツリーごと停止
        _runner?.Stop();
        _runner = null;

        // ポート 6242 を LISTEN している全プロセス (api_gui.py の python.exe を含む) を
        // プロセスツリーごと強制終了。uv.exe の子プロセスはオーファンになるため必須。
        List<int> killed = await Task.Run(() => PortKiller.KillListeners(6242)).ConfigureAwait(true);
        if (killed.Count > 0)
            AppendTerminal($"\x1b[90m[RvcRealtimeGui] ポート 6242 の使用プロセスを強制終了: PID={string.Join(",", killed)}\x1b[0m\r\n");

        _rvcModePage?.SetVcSpinner(false);
        AppendTerminal("\x1b[90m[RvcRealtimeGui] サーバーを停止しました。\x1b[0m\r\n");
        SetServerRunning(false);
    }

    // ── イベントハンドラ ────────────────────────────────────────

    async Task ToggleServerAsync()
    {
        // 子プロセスが生きている、もしくはポート 6242 が使われていれば停止フロー。
        // 外部起動 / オーファン python.exe も確実に拾えるようにする。
        bool serverAlive = _runner?.IsRunning == true
                        || PortKiller.GetListeningPids(6242).Count > 0;
        if (serverAlive)
            await StopServerProcessAsync();
        else
            StartServerProcess();

        // 起動/停止操作の直後にポーリング待ちを打ち切って即座に状態を反映し、
        // その後しばらくは短い間隔で状態変化を素早く検知する
        _fastPollUntilTick = Environment.TickCount64 + FastPollDurationMs;
        TriggerStatusPoll();
    }

    void TriggerStatusPoll()
    {
        if (_statusPollTrigger.CurrentCount == 0)
            _statusPollTrigger.Release();
    }

    // ── バックグラウンド ────────────────────────────────────────

    async Task PollServerStatusAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool alive = await _api.PingAsync(ct).ConfigureAwait(false);
            _dispatcher.TryEnqueue(() =>
            {
                _rvcModePage?.SetServerLabel(alive);
                if (alive)
                {
                    _rvcModePage?.SetVcSpinner(false);
                }
                else
                {
                    // 子プロセスが実行中（サーバー起動待ち）ならスピナーを表示
                    _rvcModePage?.SetVcSpinner(_runner?.IsRunning == true);
                }
                _rvcModePage?.SetServerConnected(alive);
                if (alive) _rvcModePage?.TryInitializeAsync();
            });
            int interval = Environment.TickCount64 < _fastPollUntilTick ? FastPollIntervalMs : NormalPollIntervalMs;
            try { await _statusPollTrigger.WaitAsync(interval, ct).ConfigureAwait(false); } catch { return; }
        }
    }
}
