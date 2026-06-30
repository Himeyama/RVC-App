using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RvcRealtimeGui.Models;
using RvcRealtimeGui.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace RvcRealtimeGui;

public sealed partial class MainWindow : Window
{
    readonly RvcApiClient _api = new();
    readonly DispatcherQueue _dispatcher;
    CancellationTokenSource? _metricsCts;
    CancellationTokenSource? _statusCts;
    bool _suppressHostApiChanged;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarGrid);
        try { AppWindow.SetIcon("Assets/AppIcon.ico"); } catch { }

        _dispatcher = DispatcherQueue.GetForCurrentThread();

        Activated += async (_, _) => await InitializeAsync();
        Closed += (_, _) =>
        {
            _metricsCts?.Cancel();
            _statusCts?.Cancel();
            _api.Dispose();
        };
    }

    async Task InitializeAsync()
    {
        if (_statusCts is not null) return;  // 二重初期化防止
        _statusCts = new CancellationTokenSource();
        _ = PollServerStatusAsync(_statusCts.Token);

        if (!await _api.PingAsync().ConfigureAwait(true))
        {
            ServerLabel.Text = "未接続 (api_gui.py を起動してください)";
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

    // ── イベントハンドラ ────────────────────────────────────────

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

    async void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            RvcConfig cfg = CollectConfig();
            StartResponse res = await _api.StartAsync(cfg).ConfigureAwait(true);
            SrLabel.Text = res.Samplerate.ToString();
            DelayLabel.Text = $"{res.DelayMs} ms";
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    async void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        try { await _api.StopAsync().ConfigureAwait(true); }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

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
                    await Task.Delay(2000, ct).ConfigureAwait(false);  // 切断時は再試行
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
                }
                else
                {
                    ServerLabel.Text = "未接続";
                    ServerLabel.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
                }
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
