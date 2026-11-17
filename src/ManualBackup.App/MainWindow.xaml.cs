using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ManualBackup.Core;
using Microsoft.Win32;

namespace ManualBackup.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly SettingsStore _store = new();
    private readonly BackupEngine _engine = new();
    private readonly MultiBackupEngine _multiEngine = new();
    private ObservableCollection<BackupSourceEntry>? _observedSourceCollection;
    private readonly HashSet<BackupSourceEntry> _observedSources = [];
    private readonly HashSet<BackupSourceEntry> _pendingSourceNames = [];
    private BackupSourceEntry? _selectedSourceEntry;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private CancellationTokenSource? _cancellation;
    private BackupProfile? _selectedProfile;
    private bool _isBusy;
    private bool _closeAfterWork;
    private bool _isRestore;
    private BackupPlan? _reviewedRestorePlan;
    private string? _lastLogPath;
    private string? _currentLogPath;
    private string _newCount = "—", _updatedCount = "—", _unchangedCount = "—", _totalSize = "—";
    private string _statusText = "コピー元と保存先を指定してください。";
    private string _detailText = "バックアップ先には専用のフォルダーを指定してください。";
    private string _emptyMessage = "「差分を確認」でコピー対象を表示します。";
    private string _logText = "", _saveStatus = "設定は自動保存されます。";
    private bool _isScanning;
    private double _progressPercent;

    public ObservableCollection<BackupProfile> Profiles { get; } = [];
    public ObservableCollection<PreviewRow> PreviewRows { get; } = [];
    public IReadOnlyList<int> ParallelSourceChoices { get; } = Enumerable.Range(1, 8).ToArray();
    public BackupProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (_selectedProfile == value) return;
            if (_selectedProfile is not null) _selectedProfile.PropertyChanged -= Profile_PropertyChanged;
            _selectedProfile = value;
            if (value is not null) value.PropertyChanged += Profile_PropertyChanged;
            ObserveSourceFolders(); SelectedSourceEntry = null;
            Notify(); NotifyActivePaths(); ClearPreview();
        }
    }
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; Notify(); Notify(nameof(CanEdit)); Notify(nameof(CanRun)); Notify(nameof(CanExecute)); Notify(nameof(CanRemoveSource)); } }
    public bool CanEdit => !IsBusy;
    public bool CanRun => !IsBusy && SelectedProfile is not null && !string.IsNullOrWhiteSpace(ActiveDestinationPath) &&
        (IsRestore ? !string.IsNullOrWhiteSpace(ActiveSourcePath) : SelectedProfile.SourceFolders.Count > 0 &&
            SelectedProfile.SourceFolders.All(s => !string.IsNullOrWhiteSpace(s.SourcePath)) &&
            (SelectedProfile.SourceFolders.Count == 1 || SelectedProfile.SourceFolders.All(s => !string.IsNullOrWhiteSpace(s.TargetFolderName))));
    public bool CanExecute => CanRun && (!IsRestore || _reviewedRestorePlan is { SkippedPaths.Count: 0 });
    public bool IsRestore
    {
        get => _isRestore;
        set
        {
            if (_isRestore == value || IsBusy) return;
            _isRestore = value;
            Notify(); Notify(nameof(IsBackup));
            foreach (var name in new[] { nameof(ModeTitle), nameof(ModeDescription), nameof(SourceLabel), nameof(DestinationLabel), nameof(SourceFieldName), nameof(DestinationFieldName), nameof(SourceTip), nameof(DestinationTip), nameof(OperationWarning), nameof(ActionInstruction), nameof(RunButtonText), nameof(RunTip), nameof(OpenDestinationText), nameof(UpdateCountLabel), nameof(BackupSourcesVisibility), nameof(RestoreSourceVisibility) }) Notify(name);
            NotifyActivePaths(); ClearPreview();
        }
    }
    public bool IsBackup { get => !IsRestore; set { if (value) IsRestore = false; } }
    public string ActiveSourcePath
    {
        get => SelectedProfile?.RestoreSourcePath ?? "";
        set { if (SelectedProfile is null || IsBusy || !IsRestore) return; SelectedProfile.RestoreSourcePath = value; }
    }
    public string ActiveDestinationPath
    {
        get => (IsRestore ? SelectedProfile?.RestoreDestinationPath : SelectedProfile?.DestinationPath) ?? "";
        set { if (SelectedProfile is null || IsBusy) return; if (IsRestore) SelectedProfile.RestoreDestinationPath = value; else SelectedProfile.DestinationPath = value; }
    }
    public string ModeTitle => IsRestore ? "差分を確認して復元" : "手動差分バックアップ";
    public string ModeDescription => IsRestore ? "復元元のフォルダーから、復元先へ追加・変更されたファイルを書き戻します。" : "複数のコピー元をまとめて、追加・変更されたファイルだけ保存します。";
    public string SourceLabel => IsRestore ? "復元元" : "コピー元";
    public string DestinationLabel => IsRestore ? "復元先" : "保存先";
    public string SourceFieldName => SourceLabel + "フォルダー";
    public string DestinationFieldName => DestinationLabel + "フォルダー";
    public string SourceTip => IsRestore ? "復元したい内容があるフォルダー（バックアップ先など）" : "バックアップしたいフォルダー";
    public string DestinationTip => IsRestore ? "ファイルを書き戻すフォルダー。同名ファイルは上書きされます。" : "バックアップ専用のフォルダーを選んでください";
    public string OperationWarning => IsRestore ? "復元先の同名ファイルを上書きします。復元先だけにあるファイルは残します。実行前に内容を照合します。" : "保存先の既存ファイルは同名の更新時に上書きします。元で削除したファイルは保存先に残ります。";
    public string ActionInstruction => IsRestore ? "差分の確認後に復元" : "差分を確認して、実行";
    public string RunButtonText => IsRestore ? "復元を実行" : "バックアップを実行";
    public string OpenDestinationText => IsRestore ? "復元先を開く" : "保存先を開く";
    public string UpdateCountLabel => IsRestore ? "上書き" : "更新";
    public string RunTip => IsRestore ? "先に「差分を確認」を実行してください。対象外・確認できない項目がある場合は復元できません。" : "最新の差分を確認してバックアップします。";
    public Visibility BackupSourcesVisibility => IsRestore ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RestoreSourceVisibility => IsRestore ? Visibility.Visible : Visibility.Collapsed;
    public string BackupLayoutHint => SelectedProfile?.SourceFolders.Any(s => string.IsNullOrWhiteSpace(s.TargetFolderName)) == true
        ? "保存先名が空欄の1件設定は、従来どおり保存先直下にコピーします。複数指定する場合は全行の保存先名を入力してください。既存の保存データは移動しません。"
        : "各コピー元を「保存先\\保存先フォルダー名」に分けます。同名のコピー元は、保存先名を別の名前にしてください。";
    public BackupSourceEntry? SelectedSourceEntry
    {
        get => _selectedSourceEntry;
        set { _selectedSourceEntry = value; Notify(); Notify(nameof(CanRemoveSource)); }
    }
    public bool CanRemoveSource => CanEdit && SelectedSourceEntry is not null;
    private void NotifyActivePaths()
    {
        Notify(nameof(ActiveSourcePath)); Notify(nameof(ActiveDestinationPath)); Notify(nameof(CanRun)); Notify(nameof(CanExecute)); Notify(nameof(BackupLayoutHint));
    }
    public bool HasLog => _lastLogPath is not null;
    public string NewCount { get => _newCount; private set { _newCount = value; Notify(); } }
    public string UpdatedCount { get => _updatedCount; private set { _updatedCount = value; Notify(); } }
    public string UnchangedCount { get => _unchangedCount; private set { _unchangedCount = value; Notify(); } }
    public string TotalSize { get => _totalSize; private set { _totalSize = value; Notify(); } }
    public string StatusText { get => _statusText; private set { _statusText = value; Notify(); } }
    public string DetailText { get => _detailText; private set { _detailText = value; Notify(); } }
    public string EmptyMessage { get => _emptyMessage; private set { _emptyMessage = value; Notify(); } }
    public string LogText { get => _logText; private set { _logText = value; Notify(); } }
    public string SaveStatus { get => _saveStatus; private set { _saveStatus = value; Notify(); } }
    public bool IsScanning { get => _isScanning; private set { _isScanning = value; Notify(); } }
    public double ProgressPercent { get => _progressPercent; private set { _progressPercent = value; Notify(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public MainWindow()
    {
        var settings = _store.Load(out var warning);
        foreach (var profile in settings.Profiles) Profiles.Add(profile);
        if (Profiles.Count == 0) Profiles.Add(new BackupProfile { Name = "マイバックアップ" });
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == settings.SelectedId) ?? Profiles[0];
        InitializeComponent();
        DataContext = this;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
        if (warning is not null) Loaded += (_, _) => MessageBox.Show(this, warning, "設定の読み込み", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Profile_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BackupProfile.SourceFolders)) { ObserveSourceFolders(); ClearPreview(); NotifyActivePaths(); }
        if (e.PropertyName is nameof(BackupProfile.SourcePath) or nameof(BackupProfile.DestinationPath) or nameof(BackupProfile.RestoreSourcePath) or nameof(BackupProfile.RestoreDestinationPath) or nameof(BackupProfile.MaxParallelSources)) { ClearPreview(); NotifyActivePaths(); }
        _saveTimer.Stop(); _saveTimer.Start();
    }
    private void ObserveSourceFolders()
    {
        if (_observedSourceCollection is not null) _observedSourceCollection.CollectionChanged -= SourceFolders_CollectionChanged;
        foreach (var source in _observedSources) source.PropertyChanged -= SourceEntry_PropertyChanged;
        _observedSources.Clear();
        _observedSourceCollection = SelectedProfile?.SourceFolders;
        if (_observedSourceCollection is null) return;
        _observedSourceCollection.CollectionChanged += SourceFolders_CollectionChanged;
        foreach (var source in _observedSourceCollection)
            if (_observedSources.Add(source)) source.PropertyChanged += SourceEntry_PropertyChanged;
    }
    private void SourceFolders_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ObserveSourceFolders(); ClearPreview(); NotifyActivePaths();
        _saveTimer.Stop(); _saveTimer.Start();
    }
    private void SourceEntry_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        ClearPreview(); NotifyActivePaths(); _saveTimer.Stop(); _saveTimer.Start();
    }
    private static string DefaultTargetName(string source)
    {
        try { return Path.IsPathFullyQualified(source) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(source))) : ""; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return ""; }
    }
    private void AddSourceFolders_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit || SelectedProfile is null) return;
        var picker = new OpenFolderDialog { Title = "コピー元フォルダーを選択（複数選択できます）", Multiselect = true };
        if (picker.ShowDialog(this) != true) return;
        foreach (var path in picker.FolderNames)
        {
            if (SelectedProfile.SourceFolders.Any(s => string.Equals(s.SourcePath.TrimEnd('\\', '/'), path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))) continue;
            var entry = new BackupSourceEntry { SourcePath = path, TargetFolderName = DefaultTargetName(path) };
            SelectedProfile.SourceFolders.Add(entry); SelectedSourceEntry = entry;
        }
    }
    private void AddSourceRow_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit || SelectedProfile is null) return;
        var entry = new BackupSourceEntry(); _pendingSourceNames.Add(entry);
        SelectedProfile.SourceFolders.Add(entry); SelectedSourceEntry = entry;
        SourceFoldersGrid.ScrollIntoView(entry);
    }
    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRemoveSource || SelectedProfile is null || SelectedSourceEntry is null) return;
        var entry = SelectedSourceEntry; _pendingSourceNames.Remove(entry);
        SelectedProfile.SourceFolders.Remove(entry); SelectedSourceEntry = null;
    }
    private void SourceFoldersGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Column.DisplayIndex != 0 || e.Row.Item is not BackupSourceEntry entry || !_pendingSourceNames.Contains(entry) || e.EditingElement is not TextBox editor) return;
        var name = DefaultTargetName(editor.Text.Trim());
        if (string.IsNullOrWhiteSpace(entry.TargetFolderName) && !string.IsNullOrWhiteSpace(name)) entry.TargetFolderName = name;
        if (!string.IsNullOrWhiteSpace(name)) _pendingSourceNames.Remove(entry);
    }
    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) SaveSettings(); }
    private void SaveSettings()
    {
        try { _store.Save(Profiles, SelectedProfile?.Id); SaveStatus = "設定を保存済み"; }
        catch (Exception ex) { SaveStatus = "設定を保存できません: " + ex.Message; }
    }
    private void ClearPreview()
    {
        _reviewedRestorePlan = null; Notify(nameof(CanExecute));
        PreviewRows.Clear(); NewCount = UpdatedCount = UnchangedCount = TotalSize = "—";
        EmptyMessage = "「差分を確認」でコピー対象を表示します。";
        if (!IsBusy) { StatusText = IsRestore ? "復元元と復元先を指定して、差分を確認してください。" : "実行の準備ができています。"; DetailText = IsRestore ? "復元ではファイル内容も照合します。確認後にフォルダーや内容を変更した場合は、差分を確認し直してください。" : "実行時は最新の差分を再確認します。"; ProgressPercent = 0; }
    }
    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var profile = new BackupProfile { Name = "フォルダー設定 " + (Profiles.Count + 1) };
        Profiles.Add(profile); SelectedProfile = profile; SaveSettings();
    }
    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null) return;
        if (MessageBox.Show(this, $"設定「{SelectedProfile.Name}」を削除しますか？\nバックアップしたファイルは削除されません。", "設定を削除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Profiles.Remove(SelectedProfile);
        if (Profiles.Count == 0) Profiles.Add(new BackupProfile { Name = "マイバックアップ" });
        SelectedProfile = Profiles[0]; SaveSettings();
    }
    private void BrowseSource_Click(object sender, RoutedEventArgs e) => BrowseFolder(true);
    private void BrowseDestination_Click(object sender, RoutedEventArgs e) => BrowseFolder(false);
    private void BrowseFolder(bool source)
    {
        if (SelectedProfile is null) return;
        var picker = new OpenFolderDialog { Title = (source ? SourceLabel : DestinationLabel) + "フォルダーを選択", Multiselect = false };
        var current = source ? ActiveSourcePath : ActiveDestinationPath;
        if (Directory.Exists(current)) picker.InitialDirectory = current;
        if (picker.ShowDialog(this) == true)
        {
            if (source) ActiveSourcePath = picker.FolderName;
            else ActiveDestinationPath = picker.FolderName;
        }
    }
    private async void Preview_Click(object sender, RoutedEventArgs e) => await PerformAsync(false);
    private async void Run_Click(object sender, RoutedEventArgs e) => await PerformAsync(true);
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel(); StatusText = "中止しています…";
    }

    private async Task PerformAsync(bool execute)
    {
        if ((execute ? !CanExecute : !CanRun) || SelectedProfile is null) return;
        var profile = SelectedProfile;
        var restore = IsRestore;
        var operation = restore ? "復元" : "バックアップ";
        var source = restore ? ActiveSourcePath.Trim() : string.Join(Environment.NewLine, profile.SourceFolders.Select(s => s.SourcePath + " → " + (string.IsNullOrWhiteSpace(s.TargetFolderName) ? "保存先直下" : s.TargetFolderName)));
        var destination = ActiveDestinationPath.Trim();
        SaveSettings(); IsBusy = true; IsScanning = true; ProgressPercent = 0;
        StatusText = restore && execute ? "復元の実行を確認してください。" : restore ? "ファイル内容を照合して、復元の差分を確認しています…" : "フォルダーの差分を確認しています…";
        DetailText = "この段階では保存先のファイルを変更しません。";
        _cancellation = new CancellationTokenSource();
        _currentLogPath = null;
        try
        {
            BackupPlan? plan = null;
            MultiBackupPlan? multiPlan = null;
            IReadOnlyList<BackupFile> files = [];
            IReadOnlyList<BackupIssue> skippedPaths = [];
            var unchangedFiles = 0;
            long totalBytes = 0;
            if (restore && execute)
            {
                plan = _reviewedRestorePlan ?? throw new InvalidOperationException("先に復元の差分を確認してください。");
                files = plan.Files; skippedPaths = plan.SkippedPaths; unchangedFiles = plan.UnchangedFiles; totalBytes = plan.TotalBytes;
                var added = plan.Files.Count(f => f.Kind == BackupChangeKind.New);
                var overwritten = plan.Files.Count(f => f.Kind == BackupChangeKind.Updated);
                var answer = MessageBox.Show(this,
                    $"確認した差分を復元します。復元先の同名ファイルは上書きされます。\n\n復元元:\n{plan.SourcePath}\n\n復元先:\n{plan.DestinationPath}\n\n追加 {added:N0} 件 / 上書き {overwritten:N0} 件 / {FormatSize(plan.TotalBytes)}\n復元先だけにあるファイルは残します。\n\n復元を実行しますか？",
                    "復元先への上書きを確認", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes)
                {
                    StatusText = "復元の実行を取り消しました。";
                    DetailText = "差分確認の結果は保持しています。内容を変更した場合は差分を確認し直してください。";
                    return;
                }
                _reviewedRestorePlan = null; Notify(nameof(CanExecute));
                _cancellation.Token.ThrowIfCancellationRequested();
                LogText = ""; BeginLog("復元実行", profile, plan.SourcePath, plan.DestinationPath);
                AddLog($"確認済みの差分: 追加 {added:N0} / 上書き {overwritten:N0} / 変更なし {plan.UnchangedFiles:N0} / 容量 {FormatSize(plan.TotalBytes)}");
            }
            else
            {
                _reviewedRestorePlan = null; Notify(nameof(CanExecute));
                LogText = ""; BeginLog(execute ? "バックアップ実行" : operation + " 差分確認", profile, source, destination);
                if (restore)
                {
                    plan = await _engine.CreatePlanAsync(new BackupRequest(source, destination, FileComparisonMode.Content), _cancellation.Token);
                    files = plan.Files; skippedPaths = plan.SkippedPaths; unchangedFiles = plan.UnchangedFiles; totalBytes = plan.TotalBytes;
                }
                else
                {
                    var sources = profile.SourceFolders.Select(s => new BackupSource(s.SourcePath.Trim(), s.TargetFolderName)).ToArray();
                    multiPlan = await _multiEngine.CreatePlanAsync(new MultiBackupRequest(sources, destination, profile.MaxParallelSources), _cancellation.Token);
                    files = multiPlan.Files; skippedPaths = multiPlan.SkippedPaths; unchangedFiles = multiPlan.UnchangedFiles; totalBytes = multiPlan.TotalBytes;
                    AddLog($"同時実行数: {multiPlan.MaxParallelSources}（コピー元フォルダー単位）");
                }
                _cancellation.Token.ThrowIfCancellationRequested();
                ShowPlan(files, unchangedFiles, totalBytes, skippedPaths);
                AddLog($"追加 {NewCount} / {(restore ? "上書き" : "更新")} {UpdatedCount} / 変更なし {UnchangedCount} / 容量 {TotalSize}");
                await AddIssuesAsync("対象外 / 要確認", skippedPaths);
                _cancellation.Token.ThrowIfCancellationRequested();
                if (!execute)
                {
                    if (restore && skippedPaths.Count == 0) _reviewedRestorePlan = plan;
                    StatusText = skippedPaths.Count == 0 ? operation + "の差分確認が完了しました。" : $"差分確認完了 — 対象外・確認できない項目が {skippedPaths.Count:N0} 件あります。";
                    DetailText = restore ? skippedPaths.Count == 0 ? "復元先にはまだ書き込んでいません。対象一覧を確認し「復元を実行」を押してください。内容を変えた場合は再確認が必要です。" : "復元は実行できません。対象外の理由をログで確認し、解消してから差分を確認し直してください。" : "保存先への書き込みは行っていません。バックアップを実行すると、すべてのコピー元の最新の差分を再確認します。";
                    AddLog("差分確認のみ完了（コピーなし）");
                    return;
                }
            }
            _cancellation.Token.ThrowIfCancellationRequested();
            IsScanning = true; StatusText = restore ? "復元前のファイル状態を再照合しています…" : "すべてのコピー元の状態を確認しています…";
            using var progress = new CoalescedProgress(p =>
            {
                IsScanning = false;
                ProgressPercent = p.TotalBytes > 0 ? Math.Clamp(100.0 * p.BytesCopied / p.TotalBytes, 0, 100) : p.TotalFiles > 0 ? 100.0 * p.CompletedFiles / p.TotalFiles : 0;
                if (_cancellation?.IsCancellationRequested == false) StatusText = $"{operation}中 {p.CompletedFiles:N0} / {p.TotalFiles:N0} ファイル — {FormatSize(p.BytesCopied)} / {FormatSize(p.TotalBytes)}";
                DetailText = p.RelativePath;
            });
            var result = restore
                ? await _engine.ExecuteAsync(plan!, progress, _cancellation.Token)
                : await _multiEngine.ExecuteAsync(multiPlan!, progress, _cancellation.Token);
            progress.Dispose();
            await AddIssuesAsync("エラー", result.Errors);
            var outcome = result.IsCanceled ? "中止" : result.Errors.Count > 0 ? "エラーあり" : skippedPaths.Count > 0 ? "要確認" : "完了";
            StatusText = result.IsCanceled ? $"{operation}を中止しました — コピー完了 {result.CopiedFiles:N0} 件" : result.Errors.Count > 0 ? $"エラーが {result.Errors.Count:N0} 件あります — コピー完了 {result.CopiedFiles:N0} 件" : skippedPaths.Count > 0 ? $"{operation}終了・要確認 — 対象外が {skippedPaths.Count:N0} 件あります。" : $"{operation}完了 — {result.CopiedFiles:N0} ファイルを{(restore ? "復元" : "保存")}しました。";
            DetailText = $"{FormatSize(result.BytesCopied)} / {result.Duration.TotalSeconds:N1} 秒" + (result.Errors.Count > 0 || skippedPaths.Count > 0 ? " — 理由は実行ログを確認してください。" : "");
            if (!result.IsCanceled && result.Errors.Count == 0) ProgressPercent = 100;
            if (restore) profile.RestoreLastRun = $"{DateTime.Now:yyyy/MM/dd HH:mm} · {outcome}";
            else profile.LastRun = $"{DateTime.Now:yyyy/MM/dd HH:mm} · {outcome}";
            AddLog($"{outcome}: {result.CopiedFiles:N0} ファイル / {FormatSize(result.BytesCopied)} / {result.Duration.TotalSeconds:N1} 秒");
            SaveSettings();
        }
        catch (OperationCanceledException) { StatusText = operation + "を中止しました。"; DetailText = "完了済みのコピーは書き込み先に残ります。"; AddLog("中止しました。"); }
        catch (Exception ex)
        {
            StatusText = "処理を完了できませんでした。"; DetailText = ex.Message; AddLog("[エラー] " + ex.Message);
            MessageBox.Show(this, ex.Message, operation + "の確認", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsScanning = false; IsBusy = false;
            _cancellation.Dispose(); _cancellation = null;
            if (_closeAfterWork) Close();
        }
    }

    private void ShowPlan(IReadOnlyList<BackupFile> files, int unchangedFiles, long totalBytes, IReadOnlyList<BackupIssue> skippedPaths)
    {
        NewCount = files.Count(f => f.Kind == BackupChangeKind.New).ToString("N0");
        UpdatedCount = files.Count(f => f.Kind == BackupChangeKind.Updated).ToString("N0");
        UnchangedCount = unchangedFiles.ToString("N0"); TotalSize = FormatSize(totalBytes);
        PreviewRows.Clear();
        const int displayLimit = 10000;
        foreach (var file in files.Take(displayLimit)) PreviewRows.Add(new PreviewRow(file.Kind == BackupChangeKind.New ? "追加" : IsRestore ? "上書き" : "更新", file.RelativePath, FormatSize(file.Length)));
        foreach (var issue in skippedPaths.Take(500)) PreviewRows.Add(new PreviewRow("要確認", issue.RelativePath, "対象外"));
        EmptyMessage = PreviewRows.Count > 0 ? "" : "追加・変更されたファイルはありません。";
        if (files.Count > displayLimit) AddLog($"一覧は先頭 {displayLimit:N0} 件を表示します。コピーは全 {files.Count:N0} 件が対象です。");
    }

    private void BeginLog(string action, BackupProfile profile, string source, string destination)
    {
        var logDirectory = Path.Combine(_store.DataDirectory, "Logs");
        Directory.CreateDirectory(logDirectory);
        _currentLogPath = Path.Combine(logDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..6] + ".log");
        File.WriteAllText(_currentLogPath, "Backup Desk 1.3\n", new UTF8Encoding(true));
        _lastLogPath = _currentLogPath; Notify(nameof(HasLog));
        AddLog(action + " — " + profile.Name);
        AddLog(SourceLabel + ": " + source); AddLog(DestinationLabel + ": " + destination);
    }
    private void AddLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        LogText = (LogText.Length > 100000 ? LogText[^80000..] : LogText) + line;
        if (_currentLogPath is null) return;
        try { File.AppendAllText(_currentLogPath, line, Encoding.UTF8); }
        catch (Exception ex) { SaveStatus = "ログを保存できません: " + ex.Message; _currentLogPath = null; }
    }
    private async Task AddIssuesAsync(string category, IReadOnlyList<BackupIssue> issues)
    {
        if (issues.Count == 0) return;
        var logPath = _currentLogPath;
        if (logPath is not null)
        {
            try
            {
                await Task.Run(() =>
                {
                    using var writer = new StreamWriter(logPath, true, Encoding.UTF8);
                    foreach (var issue in issues) writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{category}] {issue.RelativePath}: {issue.Message}");
                });
            }
            catch (Exception ex) { SaveStatus = "ログを保存できません: " + ex.Message; _currentLogPath = null; }
        }
        var visible = string.Join(Environment.NewLine, issues.Take(100).Select(issue => $"[{category}] {issue.RelativePath}: {issue.Message}"));
        if (issues.Count > 100) visible += $"{Environment.NewLine}…ほか {issues.Count - 100:N0} 件（全件は保存済みログを参照）";
        LogText = (LogText.Length > 80000 ? LogText[^60000..] : LogText) + visible + Environment.NewLine;
    }
    private void LogBox_TextChanged(object sender, TextChangedEventArgs e) => LogBox.ScrollToEnd();
    private void OpenLog_Click(object sender, RoutedEventArgs e) { if (_lastLogPath is not null) OpenPath(_lastLogPath); }
    private void OpenDestination_Click(object sender, RoutedEventArgs e)
    {
        var path = ActiveDestinationPath.Trim();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) { MessageBox.Show(this, DestinationLabel + "フォルダーはまだ存在しません。実行時に作成します。", OpenDestinationText); return; }
        OpenPath(path);
    }
    private void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "開けませんでした", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void Help_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "【同時実行】\n「同時実行数」で1～8を選べます。初期値は2、1は順次実行です。コピー元フォルダーを並列に処理し、1フォルダー内のファイルと差分確認は順次処理します。同じドライブでは、数を増やすと遅くなる場合もあります。設定はフォルダー設定ごとに保存します。\n\n中止すると、処理中のコピーが停止するまで待ちます。完了済みのファイルは残り、エラーがあっても他のコピー元の結果を含めてログに表示します。\n\n" +
        "【バックアップ】\n「フォルダー追加…」で複数のコピー元を選択します。「パスを入力」から一覧へ直接入力することもできます。共通の保存先を指定し、「差分を確認」→「バックアップを実行」を押します。\n\n各コピー元の中身を「保存先\\保存先フォルダー名」へ保存します。保存先フォルダー名は一覧で編集でき、複数指定ではすべて別の名前にしてください。従来の1件設定で保存先名が空欄の場合は、保存先直下を維持します。複数に増やす際は空欄を埋めてください。既存の保存データは移動しません。\n\nサイズまたは更新日時が異なるファイルだけを保存します。一覧からの削除は設定だけで、ファイルは消しません。\n\n【復元】\n上部の「復元」を選び、復元元と復元先を指定します。複数コピー元のバックアップから戻すときは、対応する保存先の子フォルダーを復元元に選びます。\n1. 「差分を確認」で内容（SHA-256）も照合します。\n2. 追加・上書き対象を確認します。\n3. 「復元を実行」でパスと件数を確認します。古い日時の内容も差分があれば上書きします。\n\n書き込み先だけのファイルは残し、世代は保持しません。復元で対象外の項目があればログを確認して解消してください。確認後に編集した場合は再確認が必要です。復元中は関連アプリを閉じ、ファイルを編集しないでください。途中のエラー・中止では完了済みのコピーが残ります。\n\nバックアップと復元のパスは別々に自動保存します。リンク・ジャンクション・OneDriveの再解析ポイントは対象外です。\n\n設定・ログ: " + _store.DataDirectory,
        "Backup Desk の使い方", MessageBoxButton.OK, MessageBoxImage.Information);
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (IsBusy)
        {
            e.Cancel = true;
            if (!_closeAfterWork && MessageBox.Show(this, "処理を中止して終了しますか？", "処理中", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                if (!IsBusy) { e.Cancel = false; SaveSettings(); return; }
                _closeAfterWork = true; _cancellation?.Cancel(); StatusText = "処理を中止して終了しています…";
            }
            return;
        }
        _saveTimer.Stop(); SaveSettings();
    }
    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes:N0} B" : $"{value:N1} {units[unit]}";
    }
}
