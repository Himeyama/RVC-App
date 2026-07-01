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
        _ = LoadUvr5ModelsAsync();
    }

    async Task LoadUvr5ModelsAsync()
    {
        try
        {
            List<string> models = await _api.GetUvr5ModelsAsync().ConfigureAwait(true);
            UvrModelCombo.ItemsSource = models;
            if (models.Count > 0) UvrModelCombo.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            AppendLog($"[UVR5] モデル一覧の取得に失敗しました: {ex.Message}");
        }
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

    // ── UVR5 ボーカル分離 ────────────────────────────────────────

    async void UvrSeparateBtn_Click(object sender, RoutedEventArgs e)
    {
        string inpRoot = UvrInpRootBox.Text.Trim();
        string modelName = UvrModelCombo.SelectedItem as string ?? "";
        if (string.IsNullOrEmpty(inpRoot))
        {
            await ShowErrorAsync("入力フォルダを指定してください。").ConfigureAwait(true);
            return;
        }
        if (string.IsNullOrEmpty(modelName))
        {
            await ShowErrorAsync("UVR5 モデルを選択してください。").ConfigureAwait(true);
            return;
        }
        string format0 = (UvrFormatCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "flac";
        string outRoot = string.IsNullOrEmpty(UvrOutRootBox.Text.Trim()) ? "opt" : UvrOutRootBox.Text.Trim();
        var req = new UvrSeparateRequest
        {
            ModelName = modelName,
            InpRoot = inpRoot,
            SaveRootVocal = outRoot,
            SaveRootIns = outRoot,
            Format0 = format0,
        };
        try
        {
            string jobId = await _api.StartUvrSeparateAsync(req).ConfigureAwait(true);
            await PollJobAsync(jobId, "UVR5分離", UvrSeparateStatus, UvrSeparateBtn).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message).ConfigureAwait(true);
        }
    }

    async void BrowseUvrInpRootBtn_Click(object sender, RoutedEventArgs e)
    {
        FolderPicker picker = new();
        IntPtr hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
        InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add("*");
        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is not null) UvrInpRootBox.Text = folder.Path;
    }

    // ── モデルマージ ─────────────────────────────────────────────

    async void MergeBtn_Click(object sender, RoutedEventArgs e)
    {
        string path1 = MergePathABox.Text.Trim();
        string path2 = MergePathBBox.Text.Trim();
        string name = MergeSaveNameBox.Text.Trim();
        if (string.IsNullOrEmpty(path1) || string.IsNullOrEmpty(path2) || string.IsNullOrEmpty(name))
        {
            await ShowErrorAsync("モデル A・モデル B・保存名をすべて指定してください。").ConfigureAwait(true);
            return;
        }
        string sr = (MergeSrCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "40k";
        string version = (MergeVersionCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "v2";
        var req = new ModelMergeRequest
        {
            Path1 = path1,
            Path2 = path2,
            Alpha1 = MergeAlphaSlider.Value,
            Sr = sr,
            F0 = MergeIfF0Check.IsChecked == true,
            Name = name,
            Version = version,
        };
        MergeBtn.IsEnabled = false;
        MergeStatus.Text = "実行中...";
        try
        {
            string message = await _api.MergeModelsAsync(req).ConfigureAwait(true);
            MergeStatus.Text = "完了";
            AppendLog($"[モデルマージ] {message}");
        }
        catch (Exception ex)
        {
            MergeStatus.Text = $"失敗: {ex.Message}";
            AppendLog($"[モデルマージ] エラー: {ex.Message}");
        }
        finally
        {
            MergeBtn.IsEnabled = true;
        }
    }

    // ── モデル情報 表示・変更 ────────────────────────────────────

    async void ShowInfoBtn_Click(object sender, RoutedEventArgs e)
    {
        string path = InfoPathBox.Text.Trim();
        if (string.IsNullOrEmpty(path))
        {
            await ShowErrorAsync("モデルパスを指定してください。").ConfigureAwait(true);
            return;
        }
        ShowInfoBtn.IsEnabled = false;
        ModelInfoStatus.Text = "実行中...";
        try
        {
            string info = await _api.GetModelInfoAsync(new ModelInfoRequest { Path = path }).ConfigureAwait(true);
            ModelInfoStatus.Text = "完了";
            AppendLog($"[モデル情報] {info}");
        }
        catch (Exception ex)
        {
            ModelInfoStatus.Text = $"失敗: {ex.Message}";
            AppendLog($"[モデル情報] エラー: {ex.Message}");
        }
        finally
        {
            ShowInfoBtn.IsEnabled = true;
        }
    }

    async void ChangeInfoBtn_Click(object sender, RoutedEventArgs e)
    {
        string path = InfoPathBox.Text.Trim();
        if (string.IsNullOrEmpty(path))
        {
            await ShowErrorAsync("モデルパスを指定してください。").ConfigureAwait(true);
            return;
        }
        var req = new ModelChangeInfoRequest { Path = path, Info = InfoNewTextBox.Text.Trim() };
        ChangeInfoBtn.IsEnabled = false;
        ModelInfoStatus.Text = "実行中...";
        try
        {
            string message = await _api.ChangeModelInfoAsync(req).ConfigureAwait(true);
            ModelInfoStatus.Text = "完了";
            AppendLog($"[モデル情報変更] {message}");
        }
        catch (Exception ex)
        {
            ModelInfoStatus.Text = $"失敗: {ex.Message}";
            AppendLog($"[モデル情報変更] エラー: {ex.Message}");
        }
        finally
        {
            ChangeInfoBtn.IsEnabled = true;
        }
    }

    // ── モデル抽出 ───────────────────────────────────────────────

    async void ExtractModelBtn_Click(object sender, RoutedEventArgs e)
    {
        string path = ExtractPathBox.Text.Trim();
        string name = ExtractSaveNameBox.Text.Trim();
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(name))
        {
            await ShowErrorAsync("checkpoint パスと保存名を指定してください。").ConfigureAwait(true);
            return;
        }
        string sr = (ExtractSrCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "40k";
        string version = (ExtractVersionCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "v2";
        var req = new ModelExtractRequest
        {
            Path = path,
            Name = name,
            Sr = sr,
            IfF0 = ExtractIfF0Check.IsChecked == true,
            Version = version,
        };
        ExtractBtn2.IsEnabled = false;
        ExtractModelStatus.Text = "実行中...";
        try
        {
            string message = await _api.ExtractModelAsync(req).ConfigureAwait(true);
            ExtractModelStatus.Text = "完了";
            AppendLog($"[モデル抽出] {message}");
        }
        catch (Exception ex)
        {
            ExtractModelStatus.Text = $"失敗: {ex.Message}";
            AppendLog($"[モデル抽出] エラー: {ex.Message}");
        }
        finally
        {
            ExtractBtn2.IsEnabled = true;
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
