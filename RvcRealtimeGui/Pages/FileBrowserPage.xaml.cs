using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace RvcRealtimeGui.Pages;

public sealed partial class FileBrowserPage : Page
{
    sealed class Entry
    {
        public required string Name { get; init; }
        public required string FullPath { get; init; }
        public required bool IsDirectory { get; init; }
        public required bool IsDrive { get; init; }
        public string Glyph => IsDrive ? "" : IsDirectory ? "" : "";
    }

    const string DrivesToken = "";
    const string AllFilesLabel = "すべてのファイル (*.*)";

    string _extensionFilter = "";
    string _activeFilter = "";
    string? _currentDir;
    bool _showingDrives;
    Action<string>? _onSelected;
    Action? _onCancelled;

    readonly List<string> _history = [];
    int _historyIndex = -1;

    bool _suppressIncrementalSearch;
    bool _suppressSelectionSync;

    public FileBrowserPage()
    {
        InitializeComponent();
    }

    public void Initialize(string extensionFilter, string? initialPath, Action<string> onSelected, Action onCancelled)
    {
        _extensionFilter = extensionFilter;
        _activeFilter = extensionFilter;
        _onSelected = onSelected;
        _onCancelled = onCancelled;

        ExtensionFilterCombo.ItemsSource = new List<string> { $"*{extensionFilter}", AllFilesLabel };
        ExtensionFilterCombo.SelectedIndex = 0;

        string? startDir = null;
        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            try
            {
                startDir = File.Exists(initialPath) ? Path.GetDirectoryName(initialPath)
                         : Directory.Exists(initialPath) ? initialPath
                         : null;
            }
            catch { }
        }
        startDir ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        _ = GoToAsync(startDir, pushHistory: true);
    }

    async void ExtensionFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _activeFilter = ExtensionFilterCombo.SelectedItem as string == AllFilesLabel ? "" : _extensionFilter;
        if (_currentDir is not null) await NavigateToAsync(_currentDir).ConfigureAwait(true);
    }

    // ── 履歴付きナビゲーション ───────────────────────────────────

    async Task GoToAsync(string target, bool pushHistory)
    {
        if (pushHistory)
        {
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            _history.Add(target);
            _historyIndex = _history.Count - 1;
        }

        if (target == DrivesToken)
        {
            await ShowDrivesAsync().ConfigureAwait(true);
        }
        else
        {
            await NavigateToAsync(target).ConfigureAwait(true);
        }
        UpdateHistoryButtons();
    }

    void UpdateHistoryButtons()
    {
        BackBtn.IsEnabled = _historyIndex > 0;
        ForwardBtn.IsEnabled = _historyIndex < _history.Count - 1;
    }

    async void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_historyIndex <= 0) return;
        _historyIndex--;
        await GoToAsync(_history[_historyIndex], pushHistory: false).ConfigureAwait(true);
    }

    async void ForwardBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_historyIndex >= _history.Count - 1) return;
        _historyIndex++;
        await GoToAsync(_history[_historyIndex], pushHistory: false).ConfigureAwait(true);
    }

    async void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_showingDrives)
        {
            await ShowDrivesAsync().ConfigureAwait(true);
        }
        else if (_currentDir is not null)
        {
            await NavigateToAsync(_currentDir).ConfigureAwait(true);
        }
    }

    async void UpBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_showingDrives || _currentDir is null) return;

        string? parent = Path.GetDirectoryName(_currentDir.TrimEnd(Path.DirectorySeparatorChar));
        await GoToAsync(string.IsNullOrEmpty(parent) ? DrivesToken : parent, pushHistory: true).ConfigureAwait(true);
    }

    async void PathBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;

        string target = PathBox.Text.Trim();
        if (!Directory.Exists(target))
        {
            await ShowMessageAsync("エラー", $"フォルダが見つかりません。\n{target}").ConfigureAwait(true);
            return;
        }
        await GoToAsync(target, pushHistory: true).ConfigureAwait(true);
    }

    async void FileNameBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        await TryConfirmFileNameAsync().ConfigureAwait(true);
    }

    void FileNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SelectBtn.IsEnabled = !string.IsNullOrWhiteSpace(FileNameBox.Text);
        if (_suppressIncrementalSearch) return;

        string prefix = FileNameBox.Text.Trim();
        if (string.IsNullOrEmpty(prefix) || EntryList.ItemsSource is not IEnumerable<Entry> entries) return;

        Entry? match = entries.FirstOrDefault(
            entry => entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (match is null) return;

        _suppressSelectionSync = true;
        EntryList.SelectedItem = match;
        EntryList.ScrollIntoView(match);
        _suppressSelectionSync = false;
    }

    async Task TryConfirmFileNameAsync()
    {
        string name = FileNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        string path = Path.IsPathRooted(name) ? name
                    : _currentDir is not null ? Path.Combine(_currentDir, name)
                    : name;

        if (!File.Exists(path))
        {
            await ShowMessageAsync("エラー", $"ファイルが見つかりません。\n{path}").ConfigureAwait(true);
            return;
        }
        _onSelected?.Invoke(path);
    }

    async Task ShowMessageAsync(string title, string message)
    {
        ContentDialog dialog = new()
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    // ── フォルダ内容の取得・表示 ─────────────────────────────────

    async Task NavigateToAsync(string dir)
    {
        try
        {
            string filter = _activeFilter;
            List<Entry> entries = await Task.Run(() =>
            {
                List<Entry> result = [];
                foreach (string sub in Directory.GetDirectories(dir).OrderBy(s => s))
                {
                    result.Add(new Entry { Name = Path.GetFileName(sub), FullPath = sub, IsDirectory = true, IsDrive = false });
                }
                foreach (string file in Directory.GetFiles(dir).OrderBy(s => s))
                {
                    if (!string.IsNullOrEmpty(filter) &&
                        !file.EndsWith(filter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    result.Add(new Entry { Name = Path.GetFileName(file), FullPath = file, IsDirectory = false, IsDrive = false });
                }
                return result;
            }).ConfigureAwait(true);

            _showingDrives = false;
            _currentDir = dir;
            PathBox.Text = dir;
            EntryList.ItemsSource = entries;
            ClearSelection();
        }
        catch (UnauthorizedAccessException)
        {
            await ShowMessageAsync("エラー", "このフォルダへアクセスできません。").ConfigureAwait(true);
        }
        catch (IOException)
        {
            await ShowMessageAsync("エラー", "このフォルダを開けません。").ConfigureAwait(true);
        }
    }

    async Task ShowDrivesAsync()
    {
        List<Entry> entries = await Task.Run(() =>
        {
            List<Entry> result = [];
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.IsReady) result.Add(new Entry { Name = d.Name, FullPath = d.Name, IsDirectory = true, IsDrive = true });
                }
                catch { }
            }
            return result;
        }).ConfigureAwait(true);

        _showingDrives = true;
        _currentDir = null;
        PathBox.Text = "";
        EntryList.ItemsSource = entries;
        ClearSelection();
    }

    void ClearSelection()
    {
        _suppressIncrementalSearch = true;
        FileNameBox.Text = "";
        _suppressIncrementalSearch = false;
        SelectBtn.IsEnabled = false;
    }

    async void EntryList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not Entry entry) return;
        if (entry.IsDirectory)
        {
            await GoToAsync(entry.FullPath, pushHistory: true).ConfigureAwait(true);
        }
        else
        {
            _onSelected?.Invoke(entry.FullPath);
        }
    }

    void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionSync) return;

        if (EntryList.SelectedItem is Entry { IsDirectory: false } entry)
        {
            _suppressIncrementalSearch = true;
            FileNameBox.Text = entry.Name;
            _suppressIncrementalSearch = false;
            SelectBtn.IsEnabled = true;
        }
        else
        {
            ClearSelection();
        }
    }

    async void SelectBtn_Click(object sender, RoutedEventArgs e) =>
        await TryConfirmFileNameAsync().ConfigureAwait(true);

    void CancelBtn_Click(object sender, RoutedEventArgs e) => _onCancelled?.Invoke();

    async void NewFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDir is null) return;

        TextBox nameBox = new() { PlaceholderText = "フォルダ名" };
        ContentDialog dialog = new()
        {
            Title = "新しいフォルダ",
            Content = nameBox,
            PrimaryButtonText = "作成",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        string name = nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        try
        {
            string newDir = Path.Combine(_currentDir, name);
            await Task.Run(() => Directory.CreateDirectory(newDir)).ConfigureAwait(true);
            await NavigateToAsync(_currentDir).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("エラー", $"フォルダを作成できませんでした。\n{ex.Message}").ConfigureAwait(true);
        }
    }
}
