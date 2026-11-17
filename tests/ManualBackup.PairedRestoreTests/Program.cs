using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ManualBackup.Core;

const long DatasetBytes = 1_073_741_824;
const int FileCount = 10_000;
const int DataCount = 1_000;
var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
if (!File.Exists(Path.Combine(workspace, "ManualBackup.sln"))) throw new InvalidOperationException("Project root missing.");
var qa = Path.Combine(workspace, ".qa");
var name = "paired-release-" + Guid.NewGuid().ToString("N");
var root = Path.GetFullPath(Path.Combine(qa, name));
var artifacts = Path.Combine(workspace, "artifacts");
var report = new ValidationReport { StartedUtc = DateTime.UtcNow, Files = FileCount, Bytes = DatasetBytes, FolderPairs = 3 };
var clock = Stopwatch.StartNew();
var backup = new MultiBackupEngine();
var restore = new MultiRestoreEngine();
var sources = Enumerable.Range(0, 3).Select(i => new BackupSource(Path.Combine(root, $"元フォルダー{i + 1}"), $"保存先{i + 1}")).ToArray();
var backupRoot = Path.Combine(root, "backup");
var pairs = sources.Select((source, index) => new RestoreFolderPair(Path.Combine(backupRoot, source.TargetFolderName), Path.Combine(root, $"復元先{index + 1}"))).ToArray();
var snapshots = new Dictionary<string, Snapshot>[3];
var directories = new HashSet<string>[3];
var extras = new Dictionary<string, Snapshot>[3];
var baselineTime = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);
try
{
    Check(!Directory.Exists(root), "fixture is new");
    Check(!Directory.Exists(qa) || (File.GetAttributes(qa) & FileAttributes.ReparsePoint) == 0, "scratch root is not a link");
    Directory.CreateDirectory(root);
    await Phase("create-3-sources-10000-files-1GiB", () =>
    {
        foreach (var source in sources) Directory.CreateDirectory(Path.Combine(source.SourcePath, "空フォルダー", "子"));
        var random = new Random(20261117);
        for (var index = 0; index < FileCount; index++)
        {
            var data = index < DataCount ? new byte[(int)(DatasetBytes / DataCount + (index < DatasetBytes % DataCount ? 1 : 0))] : [];
            random.NextBytes(data);
            var path = DummyPath(sources[index % 3].SourcePath, index);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
            File.SetLastWriteTimeUtc(path, baselineTime.AddSeconds(index));
        }
        long total = 0;
        for (var row = 0; row < 3; row++)
        {
            snapshots[row] = Inventory(sources[row].SourcePath);
            directories[row] = Directories(sources[row].SourcePath);
            extras[row] = new(StringComparer.OrdinalIgnoreCase);
            total += snapshots[row].Values.Sum(value => value.Length);
        }
        Check(snapshots.Sum(set => set.Count) == FileCount && total == DatasetBytes, "exact dataset size");
        return Task.FromResult<object>(new { Files = FileCount, Bytes = total, EmptyFiles = 9000, DataFiles = DataCount });
    });
    await Phase("parallel-backup-and-verify-all-hashes", async () =>
    {
        var plan = await backup.CreatePlanAsync(new MultiBackupRequest(sources, backupRoot, 2));
        Check(plan.Files.Count == FileCount && plan.TotalBytes == DatasetBytes && plan.SkippedPaths.Count == 0, "backup preview");
        Check(!Directory.Exists(backupRoot), "backup preview is read only");
        var result = await backup.ExecuteAsync(plan, new ConsoleProgress("backup"));
        Check(result.Success && result.CopiedFiles == FileCount && result.BytesCopied == DatasetBytes, "backup copy");
        for (var row = 0; row < 3; row++) Verify(pairs[row].SourcePath, snapshots[row], directories[row], extras[row]);
        Check((await backup.CreatePlanAsync(new MultiBackupRequest(sources, backupRoot, 2))).Files.Count == 0, "unchanged backup is zero");
        return new { result.CopiedFiles, result.BytesCopied, VerifiedSha256 = FileCount, ParallelSources = 2 };
    });
    await Phase("restore-3-pairs-10000-files-1GiB-and-verify", async () =>
    {
        var plan = await restore.CreatePlanAsync(pairs);
        Check(plan.Files.Count == FileCount && plan.TotalBytes == DatasetBytes && plan.SkippedPaths.Count == 0, "full restore preview");
        Check(pairs.All(pair => !Directory.Exists(pair.DestinationPath)), "restore preview creates no targets");
        var result = await restore.ExecuteAsync(plan, new ConsoleProgress("restore"));
        Check(result.Success && result.CopiedFiles == FileCount && result.BytesCopied == DatasetBytes, "full restore result");
        for (var row = 0; row < 3; row++) Verify(pairs[row].DestinationPath, snapshots[row], directories[row], extras[row]);
        Check((await restore.CreatePlanAsync(pairs)).Files.Count == 0, "unchanged restore is zero");
        return new { result.CopiedFiles, result.BytesCopied, VerifiedSha256 = FileCount, RestoredEmptyFolders = true };
    });
    await Phase("restore-delta-10-missing-20-edits-retain-extra-files", async () =>
    {
        for (var index = 0; index < 30; index++)
        {
            var path = DummyPath(pairs[index % 3].DestinationPath, index);
            if (index < 10) File.Delete(path); else Edit(path, preserveTimestamp: index < 20);
        }
        for (var row = 0; row < 3; row++)
        {
            var path = Path.Combine(pairs[row].DestinationPath, "復元先だけ.txt");
            File.WriteAllText(path, "keep this file " + row);
            extras[row].Add("復元先だけ.txt", ReadSnapshot(path));
            Directory.Delete(Path.Combine(pairs[row].DestinationPath, "空フォルダー", "子"));
        }
        var before = pairs.Select(pair => Inventory(pair.DestinationPath)).ToArray();
        var plan = await restore.CreatePlanAsync(pairs);
        Check(plan.Files.Count(file => file.Kind == BackupChangeKind.New) == 10 && plan.Files.Count(file => file.Kind == BackupChangeKind.Updated) == 20 &&
            plan.UnchangedFiles == 9970 && plan.SkippedPaths.Count == 0, "exact delta including same-metadata edits");
        for (var row = 0; row < 3; row++) Check(Inventory(pairs[row].DestinationPath).OrderBy(p => p.Key).SequenceEqual(before[row].OrderBy(p => p.Key)), "preview leaves data unchanged");
        var result = await restore.ExecuteAsync(plan);
        Console.WriteLine("DELTA_RESULT " + JsonSerializer.Serialize(result));
        Check(result.Success && result.CopiedFiles == 30, "delta restore result");
        for (var row = 0; row < 3; row++) Verify(pairs[row].DestinationPath, snapshots[row], directories[row], extras[row]);
        var repeat = await restore.CreatePlanAsync(pairs);
        Check(repeat.Files.Count == 0 && repeat.UnchangedFiles == FileCount, "post-delta restore is zero");
        return new { New = 10, Updated = 20, SameMetadataContentEdits = 10, RetainedExtraFiles = 3, VerifiedSha256 = FileCount, RepeatCopied = 0 };
    });
    await Phase("later-pair-edit-blocks-all-earlier-writes", async () =>
    {
        var firstTarget = DummyPath(pairs[0].DestinationPath, 0);
        var lastSource = DummyPath(pairs[2].SourcePath, 2);
        Edit(firstTarget, true);
        var unchangedEarlierTarget = ReadSnapshot(firstTarget);
        var plan = await restore.CreatePlanAsync(pairs);
        Check(plan.Files.Count == 1, "earlier row needs one update");
        var savedBytes = File.ReadAllBytes(lastSource);
        var savedTime = File.GetLastWriteTimeUtc(lastSource);
        Edit(lastSource, true);
        var result = await restore.ExecuteAsync(plan);
        Console.WriteLine("REJECTION_RESULT " + JsonSerializer.Serialize(result));
        Check(!result.Success && result.CopiedFiles == 0 && result.Errors.Any(issue => issue.RelativePath.StartsWith("[3]", StringComparison.Ordinal)), "late source change rejects complete batch");
        Check(ReadSnapshot(firstTarget) == unchangedEarlierTarget, "earlier target never written");
        File.WriteAllBytes(lastSource, savedBytes); File.SetLastWriteTimeUtc(lastSource, savedTime);
        var repaired = await restore.ExecuteAsync(await restore.CreatePlanAsync(pairs));
        Console.WriteLine("REPAIR_RESULT " + JsonSerializer.Serialize(repaired));
        Check(repaired.Success && repaired.CopiedFiles == 1, "fresh preview restores pending update");
        for (var row = 0; row < 3; row++) Verify(pairs[row].DestinationPath, snapshots[row], directories[row], extras[row]);
        Check(!Directory.GetFiles(root, ".manualbackup-*.tmp", SearchOption.AllDirectories).Any(), "no staging leftovers");
        return new { RejectedBeforeAnyWrite = true, result.CopiedFiles, RecoveredAfterFreshPreview = true, VerifiedSha256 = FileCount };
    });
    report.Passed = true;
}
catch (Exception exception) { report.Error = exception.ToString(); Console.Error.WriteLine("FAIL " + exception); }
finally
{
    try
    {
        var resolved = Path.GetFullPath(root);
        var expected = Path.Combine(Path.GetFullPath(qa), name);
        Check(resolved == expected && resolved.StartsWith(Path.GetFullPath(qa) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "cleanup path is isolated fixture");
        if (Directory.Exists(resolved))
        {
            Check((File.GetAttributes(qa) & FileAttributes.ReparsePoint) == 0, "cleanup parent is not a link");
            var pending = new Stack<string>(); pending.Push(resolved);
            while (pending.TryPop(out var current))
            {
                var attributes = File.GetAttributes(current);
                Check((attributes & FileAttributes.ReparsePoint) == 0, "cleanup refuses links");
                if ((attributes & FileAttributes.Directory) != 0)
                    foreach (var child in Directory.EnumerateFileSystemEntries(current)) pending.Push(child);
            }
            Directory.Delete(resolved, true);
        }
        report.CleanupConfirmed = !Directory.Exists(root);
    }
    catch (Exception exception) { report.Passed = false; report.CleanupError = exception.ToString(); }
    report.FinishedUtc = DateTime.UtcNow; report.TotalSeconds = clock.Elapsed.TotalSeconds;
    Directory.CreateDirectory(artifacts);
    File.WriteAllText(Path.Combine(artifacts, "paired-restore-validation.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    var markdown = new StringBuilder("# Backup Desk 1.4.0 dummy-file validation\n\n");
    markdown.AppendLine($"Result: {(report.Passed ? "PASS" : "FAIL")}. Cleanup confirmed: {report.CleanupConfirmed}. UTC: {report.FinishedUtc:O}.");
    markdown.AppendLine("\nThree paired folders; 10,000 real files (9,000 empty and 1,000 binary); exactly 1,073,741,824 bytes (1 GiB). Seeded ordinary writes, no sparse allocation. Production MultiBackupEngine and MultiRestoreEngine are used. All file paths, SHA-256 hashes, sizes, UTC modification times and directory sets are verified. This is engine validation, not desktop GUI automation.");
    markdown.AppendLine("\n| Phase | Seconds | Evidence |\n| --- | ---: | --- |");
    foreach (var phase in report.Phases) markdown.AppendLine($"| {phase.Name} | {phase.Seconds:F2} | {JsonSerializer.Serialize(phase.Details).Replace("|", "\\|")} |");
    markdown.AppendLine($"\nTotal: {report.TotalSeconds:F2} seconds. Dummy sources, backups and restored targets were removed: {report.CleanupConfirmed}.");
    if (report.Error is not null) markdown.AppendLine("\nError: " + report.Error);
    if (report.CleanupError is not null) markdown.AppendLine("\nCleanup error: " + report.CleanupError);
    File.WriteAllText(Path.Combine(artifacts, "paired-restore-validation.md"), markdown.ToString());
    Console.WriteLine($"RESULT passed={report.Passed}, cleanup={report.CleanupConfirmed}, seconds={report.TotalSeconds:F2}");
}
return report.Passed && report.CleanupConfirmed ? 0 : 1;

async Task Phase(string label, Func<Task<object>> action)
{
    Console.WriteLine("START " + label);
    var timer = Stopwatch.StartNew(); var details = await action();
    report.Phases.Add(new(label, timer.Elapsed.TotalSeconds, details));
    Console.WriteLine($"PASS {label}: {JsonSerializer.Serialize(details)} ({timer.Elapsed.TotalSeconds:F2}s)");
}
static string DummyPath(string parent, int index) => Path.Combine(parent, $"資料{index % 30:D2}", index < 1000 ? $"データ{index:D5}.bin" : $"空{index:D5}.txt");
static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
static Snapshot ReadSnapshot(string path)
{
    var info = new FileInfo(path); using var stream = File.OpenRead(path);
    Check((info.Attributes & FileAttributes.SparseFile) == 0, "dummy files must not be sparse");
    return new(info.Length, info.LastWriteTimeUtc, Convert.ToHexString(SHA256.HashData(stream)));
}
static Dictionary<string, Snapshot> Inventory(string parent) => Directory.EnumerateFiles(parent, "*", SearchOption.AllDirectories)
    .ToDictionary(path => Path.GetRelativePath(parent, path), ReadSnapshot, StringComparer.OrdinalIgnoreCase);
static HashSet<string> Directories(string parent) => Directory.EnumerateDirectories(parent, "*", SearchOption.AllDirectories)
    .Select(path => Path.GetRelativePath(parent, path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
static void Verify(string parent, Dictionary<string, Snapshot> expected, HashSet<string> directories, Dictionary<string, Snapshot> extras)
{
    var actual = Inventory(parent);
    Check(actual.Count == expected.Count + extras.Count, "file count: " + parent);
    foreach (var (path, value) in expected.Concat(extras)) Check(actual.TryGetValue(path, out var found) && found == value, "hash/size/timestamp: " + path);
    Check(Directories(parent).SetEquals(directories), "directory structure including empty folders: " + parent);
}
static void Edit(string path, bool preserveTimestamp)
{
    var time = File.GetLastWriteTimeUtc(path);
    using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var first = file.ReadByte(); file.Position = 0; file.WriteByte((byte)(first ^ 255)); file.Flush(true);
    }
    File.SetLastWriteTimeUtc(path, preserveTimestamp ? time : time.AddMinutes(1));
}
sealed record Snapshot(long Length, DateTime LastWriteTimeUtc, string Sha256);
sealed record PhaseResult(string Name, double Seconds, object Details);
sealed class ValidationReport
{
    public bool Passed { get; set; }
    public bool CleanupConfirmed { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public int FolderPairs { get; set; }
    public int Files { get; set; }
    public long Bytes { get; set; }
    public double TotalSeconds { get; set; }
    public string? Error { get; set; }
    public string? CleanupError { get; set; }
    public List<PhaseResult> Phases { get; } = [];
}
sealed class ConsoleProgress(string label) : IProgress<BackupProgress>
{
    private long _last = Stopwatch.GetTimestamp();
    public void Report(BackupProgress progress)
    {
        if (Stopwatch.GetElapsedTime(_last).TotalSeconds < 10) return;
        _last = Stopwatch.GetTimestamp(); Console.WriteLine($"{label}: {progress.CompletedFiles}/{progress.TotalFiles} files, {progress.BytesCopied}/{progress.TotalBytes} bytes");
    }
}
