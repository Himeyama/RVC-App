using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using RvcRealtimeGui.Models;
using RvcRealtimeGui.Services;
using System.Text.Json;
using Windows.Storage.Pickers;
using WinRT.Interop;

// WinUI3 WebView2 は Microsoft.UI.Xaml.Controls.WebView2

namespace RvcRealtimeGui;

public sealed partial class MainWindow : Window
{
    readonly RvcApiClient _api = new();
    readonly DispatcherQueue _dispatcher;
    CancellationTokenSource? _metricsCts;
    CancellationTokenSource? _statusCts;
    bool _suppressHostApiChanged;
    bool _vcRunning;

    ProcessRunner? _runner;
    bool _terminalReady;

    static readonly string[] _spinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    int _spinnerIndex;
    readonly Microsoft.UI.Xaml.DispatcherTimer _spinnerTimer = new()
        { Interval = TimeSpan.FromMilliseconds(80) };

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

        _spinnerTimer.Tick += (_, _) =>
        {
            _spinnerIndex = (_spinnerIndex + 1) % _spinnerFrames.Length;
            VcSpinner.Text = _spinnerFrames[_spinnerIndex];
        };

        _ = InitTerminalAsync();
    }

    // ── 初期化 ──────────────────────────────────────────────────

    async Task InitializeAsync()
    {
        if (_statusCts is not null) return;
        _statusCts = new CancellationTokenSource();
        _ = PollServerStatusAsync(_statusCts.Token);

        if (!await _api.PingAsync().ConfigureAwait(true))
        {
            ServerLabel.Text = "未接続";
            return;
        }

        ServerLabel.Text = "接続済み";
        ServerLabel.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];

        await LoadInitialDataAsync().ConfigureAwait(true);
        StartMetricsListener();
    }

    async Task LoadInitialDataAsync()
    {
        try
        {
            List<string> hostapis = await _api.GetHostApisAsync().ConfigureAwait(true);
            RvcConfig cfg = await _api.GetConfigAsync().ConfigureAwait(true);

            _suppressHostApiChanged = true;
            HostApiCombo.ItemsSource = hostapis;
            HostApiCombo.SelectedItem = hostapis.Contains(cfg.HostApi) ? cfg.HostApi : (hostapis.FirstOrDefault() ?? "");
            _suppressHostApiChanged = false;

            await RefreshDevicesAsync(cfg).ConfigureAwait(true);
            ApplyConfigToUi(cfg);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"初期化に失敗しました: {ex.Message}");
        }
    }

    async Task RefreshDevicesAsync(RvcConfig? cfg = null)
    {
        string? hostapi = HostApiCombo.SelectedItem as string;
        DevicesResponse devs = await _api.GetDevicesAsync(hostapi).ConfigureAwait(true);
        InputDeviceCombo.ItemsSource = devs.Inputs;
        OutputDeviceCombo.ItemsSource = devs.Outputs;
        string? wantIn = cfg?.InputDevice;
        string? wantOut = cfg?.OutputDevice;
        InputDeviceCombo.SelectedItem = !string.IsNullOrEmpty(wantIn) && devs.Inputs.Contains(wantIn)
            ? wantIn : devs.Inputs.FirstOrDefault();
        OutputDeviceCombo.SelectedItem = !string.IsNullOrEmpty(wantOut) && devs.Outputs.Contains(wantOut)
            ? wantOut : devs.Outputs.FirstOrDefault();
    }

    void ApplyConfigToUi(RvcConfig cfg)
    {
        PthPathBox.Text = cfg.PthPath;
        IndexPathBox.Text = cfg.IndexPath;
        WasapiExclusiveCheck.IsChecked = cfg.WasapiExclusive;
        SrModelRadio.IsChecked = cfg.SrType == "sr_model";
        SrDeviceRadio.IsChecked = cfg.SrType == "sr_device";

        ThresholdSlider.Value = cfg.Threshold;
        PitchSlider.Value = cfg.Pitch;
        FormantSlider.Value = cfg.Formant;
        IndexRateSlider.Value = cfg.IndexRate;
        RmsMixSlider.Value = cfg.RmsMixRate;
        BlockTimeSlider.Value = cfg.BlockTime;
        NCpuSlider.Value = cfg.NCpu;
        CrossfadeSlider.Value = cfg.CrossfadeLength;
        ExtraTimeSlider.Value = cfg.ExtraTime;
        INoiseCheck.IsChecked = cfg.InputNoiseReduce;
        ONoiseCheck.IsChecked = cfg.OutputNoiseReduce;
        UsePvCheck.IsChecked = cfg.UsePv;

        switch (cfg.F0Method)
        {
            case "pm":      F0PmRadio.IsChecked = true; break;
            case "harvest": F0HarvestRadio.IsChecked = true; break;
            case "crepe":   F0CrepeRadio.IsChecked = true; break;
            case "rmvpe":   F0RmvpeRadio.IsChecked = true; break;
            default:        F0FcpeRadio.IsChecked = true; break;
        }
    }

    RvcConfig CollectConfig()
    {
        string f0 = F0PmRadio.IsChecked == true ? "pm"
                  : F0HarvestRadio.IsChecked == true ? "harvest"
                  : F0CrepeRadio.IsChecked == true ? "crepe"
                  : F0RmvpeRadio.IsChecked == true ? "rmvpe"
                  : "fcpe";
        return new RvcConfig
        {
            PthPath           = PthPathBox.Text,
            IndexPath         = IndexPathBox.Text,
            HostApi           = HostApiCombo.SelectedItem as string ?? "",
            WasapiExclusive   = WasapiExclusiveCheck.IsChecked == true,
            InputDevice       = InputDeviceCombo.SelectedItem as string ?? "",
            OutputDevice      = OutputDeviceCombo.SelectedItem as string ?? "",
            SrType            = SrModelRadio.IsChecked == true ? "sr_model" : "sr_device",
            Threshold         = (int)ThresholdSlider.Value,
            Pitch             = (int)PitchSlider.Value,
            Formant           = FormantSlider.Value,
            IndexRate         = IndexRateSlider.Value,
            RmsMixRate        = RmsMixSlider.Value,
            BlockTime         = BlockTimeSlider.Value,
            CrossfadeLength   = CrossfadeSlider.Value,
            ExtraTime         = ExtraTimeSlider.Value,
            NCpu              = (int)NCpuSlider.Value,
            F0Method          = f0,
            UsePv             = UsePvCheck.IsChecked == true,
            InputNoiseReduce  = INoiseCheck.IsChecked == true,
            OutputNoiseReduce = ONoiseCheck.IsChecked == true,
        };
    }

    // ── 変換状態 UI 更新 ────────────────────────────────────────

    void SetVcRunning(bool running)
    {
        _vcRunning = running;
        if (running)
        {
            ToggleVcText.Text = "音声変換 停止";
            ToolTipService.SetToolTip(ToggleVcBtn, "音声変換 停止");
            VcStatusLabel.Text = "変換中";
            VcStatusLabel.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
        }
        else
        {
            ToggleVcText.Text = "音声変換 開始";
            ToolTipService.SetToolTip(ToggleVcBtn, "音声変換 開始");
            DelayLabel.Text = "— ms";
            InferLabel.Text = "— ms";
            VcStatusLabel.Text = "停止中";
            VcStatusLabel.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        }
    }

    // ── ターミナル (WebView2 + xterm.js) ────────────────────────

    async Task InitTerminalAsync()
    {
        await TerminalWebView.EnsureCoreWebView2Async();
        TerminalWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

        string htmlPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly()!.Location)!,
            "Assets", "terminal.html");
        // file:// URI として渡す（WebView2 はローカルファイルを読み込める）
        TerminalWebView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
    }

    void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(e.WebMessageAsJson);
            string? type = doc.RootElement.GetProperty("type").GetString();
            if (type == "ready")
            {
                _terminalReady = true;
            }
        }
        catch { }
    }

    void TerminalWebView_NavigationCompleted(
        WebView2 sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        _terminalReady = true;
    }

    void AppendTerminal(string raw)
    {
        if (!_terminalReady) return;
        // JSON.stringify 相当のエスケープを C# 側で行う
        string escaped = JsonSerializer.Serialize(raw);
        _ = TerminalWebView.CoreWebView2?.ExecuteScriptAsync($"writeTerminal({escaped})");
    }

    void SetServerRunning(bool running)
    {
        if (running)
        {
            ToggleServerText.Text = "サーバー停止";
            ToggleServerIcon.Glyph = "";  // Stop
            ToolTipService.SetToolTip(ToggleServerBtn, "api_gui.py を停止");
        }
        else
        {
            ToggleServerText.Text = "サーバー起動";
            ToggleServerIcon.Glyph = "";  // Play/Server
            ToolTipService.SetToolTip(ToggleServerBtn, "api_gui.py を起動");
        }
    }

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
            SetVcSpinner(true);
        }
        catch (Exception ex)
        {
            AppendTerminal($"\x1b[31m[RvcRealtimeGui] 起動失敗: {ex.Message}\x1b[0m\r\n");
            _runner?.Dispose();
            _runner = null;
        }
    }

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
        _metricsCts?.Cancel();
        _statusCts?.Cancel();
        await StopServerProcessAsync();
        _api.Dispose();
        // クリーンアップ完了後にウィンドウを破棄
        AppWindow.Closing -= OnWindowClosing;
        AppWindow.Destroy();
    }

    async Task StopServerProcessAsync()
    {
        if (_vcRunning)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await _api.StopAsync(cts.Token).ConfigureAwait(true); } catch { }
            SetVcRunning(false);
        }

        // 子プロセス (uv.exe → python.exe) をツリーごと停止
        _runner?.Stop();
        _runner = null;

        // ポート 6242 を LISTEN している全プロセス (api_gui.py の python.exe を含む) を
        // プロセスツリーごと強制終了。uv.exe の子プロセスはオーファンになるため必須。
        List<int> killed = await Task.Run(() => PortKiller.KillListeners(6242)).ConfigureAwait(true);
        if (killed.Count > 0)
            AppendTerminal($"\x1b[90m[RvcRealtimeGui] ポート 6242 の使用プロセスを強制終了: PID={string.Join(",", killed)}\x1b[0m\r\n");

        SetVcSpinner(false);
        AppendTerminal("\x1b[90m[RvcRealtimeGui] サーバーを停止しました。\x1b[0m\r\n");
        SetServerRunning(false);
    }

    void SetVcSpinner(bool active)
    {
        if (active)
        {
            _spinnerTimer.Start();
            VcSpinner.Visibility    = Visibility.Visible;
            ToggleVcIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            _spinnerTimer.Stop();
            VcSpinner.Visibility    = Visibility.Collapsed;
            ToggleVcIcon.Visibility = Visibility.Visible;
        }
    }

    // ── イベントハンドラ ────────────────────────────────────────

    async void ToggleServerBtn_Click(object sender, RoutedEventArgs e)
    {
        // 子プロセスが生きている、もしくはポート 6242 が使われていれば停止フロー。
        // 外部起動 / オーファン python.exe も確実に拾えるようにする。
        bool serverAlive = _runner?.IsRunning == true
                        || PortKiller.GetListeningPids(6242).Count > 0;
        if (serverAlive)
            await StopServerProcessAsync();
        else
            StartServerProcess();
    }

    void ClearTerminalBtn_Click(object sender, RoutedEventArgs e)
    {
        _ = TerminalWebView.CoreWebView2?.ExecuteScriptAsync("clearTerminal()");
    }

    async void BrowsePthBtn_Click(object sender, RoutedEventArgs e) =>
        await PickFileAsync(PthPathBox, ".pth").ConfigureAwait(true);

    async void BrowseIndexBtn_Click(object sender, RoutedEventArgs e) =>
        await PickFileAsync(IndexPathBox, ".index").ConfigureAwait(true);

    async Task PickFileAsync(TextBox target, string ext)
    {
        FileOpenPicker picker = new();
        IntPtr hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add(ext);
        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        if (file is not null) target.Text = file.Path;
    }

    async void ReloadDevicesBtn_Click(object sender, RoutedEventArgs e)
    {
        try { await RefreshDevicesAsync().ConfigureAwait(true); }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

    async void HostApiCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressHostApiChanged) return;
        try { await RefreshDevicesAsync().ConfigureAwait(true); }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

    async void ToggleVcBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_vcRunning)
        {
            try
            {
                await _api.StopAsync().ConfigureAwait(true);
                SetVcRunning(false);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(ex.Message);
            }
        }
        else
        {
            string pthPath = PthPathBox.Text.Trim();
            if (string.IsNullOrEmpty(pthPath))
            {
                await ShowErrorAsync("モデルファイル (.pth) が指定されていません。");
                return;
            }
            if (!System.IO.File.Exists(pthPath))
            {
                await ShowErrorAsync($"モデルファイルが見つかりません:\n{pthPath}");
                return;
            }
            if (InputDeviceCombo.SelectedItem is null)
            {
                await ShowErrorAsync("入力デバイスが選択されていません。");
                return;
            }
            if (OutputDeviceCombo.SelectedItem is null)
            {
                await ShowErrorAsync("出力デバイスが選択されていません。");
                return;
            }

            try
            {
                RvcConfig cfg = CollectConfig();
                StartResponse res = await _api.StartAsync(cfg).ConfigureAwait(true);
                SrLabel.Text = res.Samplerate.ToString();
                DelayLabel.Text = $"{res.DelayMs} ms";
                SetVcRunning(true);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(ex.Message);
            }
        }
    }

    // ── スライダー値表示 ────────────────────────────────────────

    void ThresholdSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        ThresholdVal.Text = ((int)e.NewValue).ToString();

    void PitchSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        PitchVal.Text = ((int)e.NewValue).ToString();

    void FormantSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        FormantVal.Text = e.NewValue.ToString("F2");

    void IndexRateSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        IndexRateVal.Text = e.NewValue.ToString("F2");

    void RmsMixSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        RmsMixVal.Text = e.NewValue.ToString("F2");

    void BlockTimeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        BlockTimeVal.Text = e.NewValue.ToString("F2");

    void NCpuSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        NCpuVal.Text = ((int)e.NewValue).ToString();

    void CrossfadeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        CrossfadeVal.Text = e.NewValue.ToString("F2");

    void ExtraTimeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        ExtraTimeVal.Text = e.NewValue.ToString("F2");

    // ── バックグラウンド ────────────────────────────────────────

    void StartMetricsListener()
    {
        _metricsCts?.Cancel();
        _metricsCts = new CancellationTokenSource();
        CancellationToken ct = _metricsCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _api.ListenMetricsAsync(metric =>
                        _dispatcher.TryEnqueue(() => InferLabel.Text = $"{metric.InferMs} ms"),
                        ct).ConfigureAwait(false);
                }
                catch when (ct.IsCancellationRequested) { return; }
                catch
                {
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                }
            }
        }, ct);
    }

    async Task PollServerStatusAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool alive = await _api.PingAsync(ct).ConfigureAwait(false);
            _dispatcher.TryEnqueue(() =>
            {
                if (alive)
                {
                    ServerLabel.Text = "接続済み";
                    ServerLabel.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
                    SetVcSpinner(false);
                }
                else
                {
                    ServerLabel.Text = "未接続";
                    ServerLabel.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
                    if (_vcRunning) SetVcRunning(false);
                    // 子プロセスが実行中（サーバー起動待ち）ならスピナーを表示
                    SetVcSpinner(_runner?.IsRunning == true);
                }
                ToggleVcBtn.IsEnabled = alive;
            });
            try { await Task.Delay(3000, ct).ConfigureAwait(false); } catch { return; }
        }
    }

    async Task ShowErrorAsync(string message)
    {
        ContentDialog dialog = new()
        {
            Title = "エラー",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
