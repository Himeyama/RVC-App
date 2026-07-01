using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using RvcRealtimeGui.Models;
using RvcRealtimeGui.Services;
using System.Text.Json;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace RvcRealtimeGui.Pages;

public sealed partial class RvcModePage : Page
{
    RvcApiClient _api = null!;
    DispatcherQueue _dispatcher = null!;

    CancellationTokenSource? _metricsCts;
    bool _suppressHostApiChanged;
    bool _vcRunning;
    bool _terminalReady;
    bool _initialized;

    static readonly string[] _spinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    int _spinnerIndex;
    readonly Microsoft.UI.Xaml.DispatcherTimer _spinnerTimer = new()
        { Interval = TimeSpan.FromMilliseconds(80) };

    public RvcModePage()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _spinnerTimer.Tick += (_, _) =>
        {
            _spinnerIndex = (_spinnerIndex + 1) % _spinnerFrames.Length;
            VcSpinner.Text = _spinnerFrames[_spinnerIndex];
        };
        _ = InitTerminalAsync();
    }

    public void Initialize(RvcApiClient api)
    {
        _api = api;
    }

    public async Task TryInitializeAsync()
    {
        if (_api is null || _initialized) return;
        bool alive = await _api.PingAsync().ConfigureAwait(true);
        if (!alive) return;

        _initialized = true;
        await LoadInitialDataAsync().ConfigureAwait(true);
        StartMetricsListener();
    }

    // ── 初期化 ──────────────────────────────────────────────────

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

    public bool IsVcRunning => _vcRunning;

    public async Task StopVcIfRunningAsync()
    {
        if (!_vcRunning) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await _api.StopAsync(cts.Token).ConfigureAwait(true); } catch { }
        SetVcRunning(false);
    }

    public void SetServerConnected(bool alive)
    {
        ToggleVcBtn.IsEnabled = alive;
        if (!alive && _vcRunning) SetVcRunning(false);
    }

    public void SetVcSpinner(bool active)
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

    // ── ターミナル (WebView2 + xterm.js) ────────────────────────

    async Task InitTerminalAsync()
    {
        await TerminalWebView.EnsureCoreWebView2Async();
        TerminalWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

        string htmlPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly()!.Location)!,
            "Assets", "terminal.html");
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

    public void AppendTerminal(string raw)
    {
        if (!_terminalReady) return;
        string escaped = JsonSerializer.Serialize(raw);
        _ = TerminalWebView.CoreWebView2?.ExecuteScriptAsync($"writeTerminal({escaped})");
    }

    void ClearTerminalBtn_Click(object sender, RoutedEventArgs e)
    {
        _ = TerminalWebView.CoreWebView2?.ExecuteScriptAsync("clearTerminal()");
    }

    bool _terminalCollapsed;

    void ToggleTerminalBtn_Click(object sender, RoutedEventArgs e)
    {
        _terminalCollapsed = !_terminalCollapsed;
        TerminalPanel.Visibility = _terminalCollapsed ? Visibility.Collapsed : Visibility.Visible;
        ToggleTerminalIcon.Glyph = _terminalCollapsed ? "" : "";
    }

    // ── イベントハンドラ ────────────────────────────────────────

    async void BrowsePthBtn_Click(object sender, RoutedEventArgs e) =>
        await PickFileAsync(PthPathBox, ".pth").ConfigureAwait(true);

    async void BrowseIndexBtn_Click(object sender, RoutedEventArgs e) =>
        await PickFileAsync(IndexPathBox, ".index").ConfigureAwait(true);

    async Task PickFileAsync(TextBox target, string ext)
    {
        FileOpenPicker picker = new();
        IntPtr hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
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

    public async void ToggleVcBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_vcRunning)
        {
            ToggleVcBtn.IsEnabled = false;
            ToggleVcText.Text = "終了中...";
            SetVcSpinner(true);
            try
            {
                await _api.StopAsync().ConfigureAwait(true);
                SetVcRunning(false);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(ex.Message);
            }
            finally
            {
                SetVcSpinner(false);
                ToggleVcBtn.IsEnabled = true;
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

            ToggleVcBtn.IsEnabled = false;
            ToggleVcText.Text = "開始中...";
            SetVcSpinner(true);
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
                ToggleVcText.Text = "音声変換 開始";
            }
            finally
            {
                SetVcSpinner(false);
                ToggleVcBtn.IsEnabled = true;
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

    async Task ShowErrorAsync(string message)
    {
        ContentDialog dialog = new()
        {
            Title = "エラー",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
