using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RvcRealtimeGui.Models;
using RvcRealtimeGui.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace RvcRealtimeGui.Pages;

public sealed partial class ModelTuningPage : Page
{
    RvcApiClient _api = null!;
    readonly DispatcherQueue _dispatcher;

    public ModelTuningPage()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    public void Initialize(ModelTuningPageContext ctx)
    {
        _api = ctx.Api;
    }

    // ── 共通入力の取得 ───────────────────────────────────────────

    string ExpDir => ExpDirBox.Text.Trim();
    string TrainsetDir => TrainsetDirBox.Text.Trim();
    string Sr => (SrCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "40k";
    string Version => (VersionCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "v2";
    bool IfF0 => IfF0Check.IsChecked == true;
    int SpkId => (int)SpkIdBox.Value;

    // ── ログ ─────────────────────────────────────────────────────

    void AppendLog(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        LogBox.Text += text.EndsWith('\n') ? text : text + "\n";
        LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null);
    }

    void ClearLogBtn_Click(object sender, RoutedEventArgs e) => LogBox.Text = "";

    // ── ジョブポーリング共通ロジック ─────────────────────────────

    async Task<bool> PollJobAsync(string jobId, string label, TextBlock statusText, Button triggerBtn)
    {
        triggerBtn.IsEnabled = false;
        statusText.Text = "実行中...";
        try
        {
            while (true)
            {
                JobStatusResponse status = await _api.GetTrainingJobAsync(jobId).ConfigureAwait(true);
                if (!string.IsNullOrEmpty(status.LogDelta))
                    AppendLog($"[{label}] {status.LogDelta}");

                if (status.Status != "running")
                {
                    bool succeeded = status.Status == "succeeded";
                    statusText.Text = succeeded ? "完了" : $"失敗: {status.Error}";
                    AppendLog($"[{label}] {(succeeded ? "完了" : $"失敗: {status.Error}")}");
                    return succeeded;
                }
                await Task.Delay(2000).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            statusText.Text = $"エラー: {ex.Message}";
            AppendLog($"[{label}] エラー: {ex.Message}");
            return false;
        }
        finally
        {
            triggerBtn.IsEnabled = true;
        }
    }

    // ── Step1 前処理 ─────────────────────────────────────────────

    async void PreprocessBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateCommon()) return;
        if (string.IsNullOrEmpty(TrainsetDir))
        {
            await ShowErrorAsync("データディレクトリを指定してください。");
            return;
        }
        var req = new PreprocessRequest
        {
            TrainsetDir = TrainsetDir,
            ExpDir = ExpDir,
            Sr = Sr,
            NP = (int)PreprocessNpBox.Value,
        };
        try
        {
            string jobId = await _api.StartPreprocessAsync(req).ConfigureAwait(true);
            await PollJobAsync(jobId, "前処理", PreprocessStatus, PreprocessBtn).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    // ── Step2 特徴抽出 ───────────────────────────────────────────

    async void ExtractBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateCommon()) return;
        string f0method = (F0MethodCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "rmvpe_gpu";
        var req = new ExtractF0FeatureRequest
        {
            ExpDir = ExpDir,
            Gpus = ExtractGpusBox.Text.Trim(),
            F0Method = f0method,
            IfF0 = IfF0,
            Version = Version,
            GpusRmvpe = ExtractGpusBox.Text.Trim(),
        };
        try
        {
            string jobId = await _api.StartExtractF0FeatureAsync(req).ConfigureAwait(true);
            await PollJobAsync(jobId, "特徴抽出", ExtractStatus, ExtractBtn).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    // ── Step3 学習 ───────────────────────────────────────────────

    async void TrainBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateCommon()) return;
        var req = new TrainRequest
        {
            ExpDir = ExpDir,
            Sr = Sr,
            IfF0 = IfF0,
            SpkId = SpkId,
            SaveEpoch = (int)SaveEpochBox.Value,
            TotalEpoch = (int)TotalEpochBox.Value,
            BatchSize = (int)BatchSizeBox.Value,
            IfSaveLatest = IfSaveLatestCheck.IsChecked == true,
            PretrainedG = PretrainedGBox.Text.Trim(),
            PretrainedD = PretrainedDBox.Text.Trim(),
            Gpus = TrainGpusBox.Text.Trim(),
            IfCacheGpu = IfCacheGpuCheck.IsChecked == true,
            IfSaveEveryWeights = IfSaveEveryWeightsCheck.IsChecked == true,
            Version = Version,
        };
        try
        {
            string jobId = await _api.StartTrainAsync(req).ConfigureAwait(true);
            await PollJobAsync(jobId, "学習", TrainStatus, TrainBtn).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    // ── Step4 インデックス作成 ───────────────────────────────────

    async void IndexBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateCommon()) return;
        var req = new TrainIndexRequest { ExpDir = ExpDir, Version = Version };
        try
        {
            string jobId = await _api.StartTrainIndexAsync(req).ConfigureAwait(true);
            await PollJobAsync(jobId, "インデックス作成", IndexStatus, IndexBtn).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    // ── ワンクリック学習 ─────────────────────────────────────────

    async void OneClickBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateCommon()) return;
        if (string.IsNullOrEmpty(TrainsetDir))
        {
            await ShowErrorAsync("データディレクトリを指定してください。");
            return;
        }
        string f0method = (F0MethodCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "rmvpe_gpu";
        var req = new Train1KeyRequest
        {
            ExpDir = ExpDir,
            TrainsetDir = TrainsetDir,
            Sr = Sr,
            IfF0 = IfF0,
            SpkId = SpkId,
            NP = (int)PreprocessNpBox.Value,
            F0Method = f0method,
            SaveEpoch = (int)SaveEpochBox.Value,
            TotalEpoch = (int)TotalEpochBox.Value,
            BatchSize = (int)BatchSizeBox.Value,
            IfSaveLatest = IfSaveLatestCheck.IsChecked == true,
            PretrainedG = PretrainedGBox.Text.Trim(),
            PretrainedD = PretrainedDBox.Text.Trim(),
            Gpus = TrainGpusBox.Text.Trim(),
            IfCacheGpu = IfCacheGpuCheck.IsChecked == true,
            IfSaveEveryWeights = IfSaveEveryWeightsCheck.IsChecked == true,
            Version = Version,
            GpusRmvpe = ExtractGpusBox.Text.Trim(),
        };
        try
        {
            string jobId = await _api.StartOneClickTrainAsync(req).ConfigureAwait(true);
            await PollJobAsync(jobId, "ワンクリック学習", OneClickStatus, OneClickBtn).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    // ── 共通 ─────────────────────────────────────────────────────

    bool ValidateCommon()
    {
        if (string.IsNullOrEmpty(ExpDir))
        {
            _ = ShowErrorAsync("実験名を指定してください。");
            return false;
        }
        return true;
    }

    async void BrowseTrainsetDirBtn_Click(object sender, RoutedEventArgs e)
    {
        FolderPicker picker = new();
        IntPtr hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
        InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add("*");
        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is not null) TrainsetDirBox.Text = folder.Path;
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

public class ModelTuningPageContext
{
    public required RvcApiClient Api { get; init; }
}
