using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using ManualBackup.Core;

namespace ManualBackup.App;

public sealed class BackupSourceEntry : INotifyPropertyChanged
{
    private string _sourcePath = "";
    private string _targetFolderName = "";
    public string SourcePath { get => _sourcePath; set { _sourcePath = value; OnChanged(); } }
    public string TargetFolderName { get => _targetFolderName; set { _targetFolderName = value; OnChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RestoreFolderEntry : INotifyPropertyChanged
{
    private string _sourcePath = "";
    private string _destinationPath = "";
    public string SourcePath { get => _sourcePath; set { _sourcePath = value; OnChanged(); } }
    public string DestinationPath { get => _destinationPath; set { _destinationPath = value; OnChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class BackupProfile : INotifyPropertyChanged
{
    public const int DefaultMaxParallelSources = 2;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    private string _name = "新しいバックアップ";
    private string _sourcePath = "";
    private ObservableCollection<BackupSourceEntry> _sourceFolders = [];
    private string _destinationPath = "";
    private int _maxParallelSources = DefaultMaxParallelSources;
    private string _lastRun = "未実行";
    private string _restoreSourcePath = "";
    private string _restoreDestinationPath = "";
    private string _restoreLastRun = "未実行";
    private ObservableCollection<RestoreFolderEntry> _restoreFolders = [];
    public string Name { get => _name; set { _name = value; OnChanged(); } }
    public string SourcePath { get => _sourcePath; set { _sourcePath = value; OnChanged(); } }
    public ObservableCollection<BackupSourceEntry> SourceFolders { get => _sourceFolders; set { _sourceFolders = value; SourceFoldersWasSet = true; OnChanged(); } }
    internal bool SourceFoldersWasSet { get; private set; }
    public string DestinationPath { get => _destinationPath; set { _destinationPath = value; OnChanged(); } }
    [JsonConverter(typeof(MaxParallelSourcesJsonConverter))]
    public int MaxParallelSources
    {
        get => _maxParallelSources;
        set
        {
            var normalized = value is >= 1 and <= 8 ? value : DefaultMaxParallelSources;
            if (_maxParallelSources == normalized) return;
            _maxParallelSources = normalized;
            OnChanged();
        }
    }
    public string LastRun { get => _lastRun; set { _lastRun = value; OnChanged(); } }
    public string RestoreSourcePath { get => _restoreSourcePath; set { _restoreSourcePath = value; OnChanged(); } }
    public string RestoreDestinationPath { get => _restoreDestinationPath; set { _restoreDestinationPath = value; OnChanged(); } }
    public string RestoreLastRun { get => _restoreLastRun; set { _restoreLastRun = value; OnChanged(); } }
    public ObservableCollection<RestoreFolderEntry> RestoreFolders { get => _restoreFolders; set { _restoreFolders = value; RestoreFoldersWasSet = true; OnChanged(); } }
    internal bool RestoreFoldersWasSet { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// A missing or null optional setting keeps older profiles usable without discarding paths.
internal sealed class MaxParallelSourcesJsonConverter : JsonConverter<int>
{
    public MaxParallelSourcesJsonConverter() { }
    public override bool HandleNull => true;
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return BackupProfile.DefaultMaxParallelSources;
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value)) return value;
        throw new JsonException("同時に処理するコピー元の数には整数を指定してください。");
    }
    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

public sealed class AppSettings
{
    public string? SelectedId { get; set; }
    public List<BackupProfile> Profiles { get; set; } = [];
}

public sealed class SettingsStore
{
    public string DataDirectory { get; } = Environment.GetEnvironmentVariable("BACKUPDESK_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BackupDesk");
    private string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public AppSettings Load(out string? warning)
    {
        warning = null;
        if (!File.Exists(SettingsPath)) return new AppSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? throw new InvalidDataException("設定が空です。");
            if (settings.Profiles is null || settings.Profiles.Any(p => p is null)) throw new InvalidDataException("設定の形式が不正です。");
            foreach (var profile in settings.Profiles)
            {
                profile.Name ??= "バックアップ";
                profile.SourcePath ??= "";
                var migrateLegacySource = !profile.SourceFoldersWasSet || profile.SourceFolders is null;
                profile.SourceFolders ??= [];
                if (profile.SourceFolders.Any(entry => entry is null))
                    throw new InvalidDataException("コピー元フォルダーの設定に不正な項目があります。");
                foreach (var entry in profile.SourceFolders)
                {
                    entry.SourcePath ??= "";
                    entry.TargetFolderName ??= "";
                }
                if (migrateLegacySource && profile.SourceFolders.Count == 0 && !string.IsNullOrWhiteSpace(profile.SourcePath))
                    profile.SourceFolders.Add(new BackupSourceEntry { SourcePath = profile.SourcePath, TargetFolderName = "" });
                // Never let the retired field resurrect sources the user has removed.
                if (profile.SourcePath.Length > 0) profile.SourcePath = "";
                profile.DestinationPath ??= "";
                profile.LastRun ??= "未実行";
                profile.RestoreSourcePath ??= "";
                profile.RestoreDestinationPath ??= "";
                var migrateLegacyRestore = !profile.RestoreFoldersWasSet || profile.RestoreFolders is null;
                profile.RestoreFolders ??= [];
                if (profile.RestoreFolders.Any(entry => entry is null))
                    throw new InvalidDataException("復元フォルダーの設定に不正な項目があります。");
                foreach (var entry in profile.RestoreFolders)
                {
                    entry.SourcePath ??= "";
                    entry.DestinationPath ??= "";
                }
                if (migrateLegacyRestore && profile.RestoreFolders.Count == 0 &&
                    (!string.IsNullOrWhiteSpace(profile.RestoreSourcePath) || !string.IsNullOrWhiteSpace(profile.RestoreDestinationPath)))
                    profile.RestoreFolders.Add(new RestoreFolderEntry { SourcePath = profile.RestoreSourcePath, DestinationPath = profile.RestoreDestinationPath });
                profile.RestoreLastRun ??= "未実行";
                if (string.IsNullOrWhiteSpace(profile.Id)) profile.Id = Guid.NewGuid().ToString("N");
            }
            return settings;
        }
        catch (Exception ex)
        {
            warning = "保存済みの設定を読み込めませんでした。新しい設定で開始します。\n" + ex.Message;
            try { File.Copy(SettingsPath, SettingsPath + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), false); } catch { }
            return new AppSettings();
        }
    }

    public void Save(IEnumerable<BackupProfile> profiles, string? selectedId)
    {
        Directory.CreateDirectory(DataDirectory);
        var temporaryPath = Path.Combine(DataDirectory, "settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var profileList = profiles.ToList();
            foreach (var profile in profileList)
                if (!string.IsNullOrEmpty(profile.SourcePath)) profile.SourcePath = "";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new AppSettings { Profiles = profileList, SelectedId = selectedId }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, SettingsPath, true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
}

public sealed record PreviewRow(string Status, string RelativePath, string Size);

// Keep only the latest chunk update instead of queueing one UI callback per file/chunk.
internal sealed class CoalescedProgress : IProgress<BackupProgress>, IDisposable
{
    private readonly object _gate = new();
    private readonly DispatcherTimer _timer;
    private BackupProgress? _latest;
    public CoalescedProgress(Action<BackupProgress> update)
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) =>
        {
            BackupProgress? value;
            lock (_gate) { value = _latest; _latest = null; }
            if (value is not null) update(value);
        };
        _timer.Start();
    }
    public void Report(BackupProgress value) { lock (_gate) _latest = value; }
    public void Dispose() => _timer.Stop();
}
