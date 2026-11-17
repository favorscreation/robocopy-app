using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ManualBackup.Core;

const long OneGiB = 1_073_741_824;
const int TotalFiles = 10_000;
const int EmptyFiles = 9_000;
const int DataFiles = 1_000;
var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
if (!File.Exists(Path.Combine(workspace, "ManualBackup.sln")))
    throw new InvalidOperationException("Run the load validation from this project's build output.");
var qaRoot = Path.GetFullPath(Path.Combine(workspace, ".qa"));
var runName = "load-engine-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
var scratchRoot = Path.GetFullPath(Path.Combine(qaRoot, runName));
var report = new RunReport
{
    StartedUtc = DateTime.UtcNow,
    ScratchRoot = scratchRoot,
    ExactDatasetBytes = OneGiB,
    Notes = ["1 GiB = 1,073,741,824 bytes; normal, non-sparse writes of seeded random bytes.",
        "The production BackupEngine is used via a project reference. This is an engine integration test, not GUI automation.",
        "All source and destination file hashes, lengths, UTC timestamps and directory sets are compared."]
};
var engine = new BackupEngine();
var timer = Stopwatch.StartNew();
var failed = false;
try
{
    Assert(!Directory.Exists(qaRoot) || (File.GetAttributes(qaRoot) & FileAttributes.ReparsePoint) == 0,
        "The scratch parent must not be a reparse point.");
    Assert(!Directory.Exists(scratchRoot), "Scratch root must be new.");
    Directory.CreateDirectory(scratchRoot);
    var emptySource = Path.Combine(scratchRoot, "empty-source");
    var emptyDestination = Path.Combine(scratchRoot, "empty-backup");
    await Phase("empty-create-10000", () =>
    {
        Directory.CreateDirectory(emptySource);
        for (var i = 0; i < TotalFiles; i++)
        {
            var path = Path.Combine(emptySource, $"フォルダー {i % 100:D3}", $"空ファイル {i:D5}.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i));
        }
        Directory.CreateDirectory(Path.Combine(emptySource, "空のフォルダー", "さらに空"));
        return Task.FromResult(new { Files = TotalFiles, Bytes = 0 });
    });
    await CopyAndVerify("empty", emptySource, emptyDestination, TotalFiles, 0);

    var mixedSource = Path.Combine(scratchRoot, "mixed-source");
    var mixedDestination = Path.Combine(scratchRoot, "mixed-backup");
    await Phase("mixed-create-10000-exact-1GiB", () =>
    {
        Directory.CreateDirectory(mixedSource);
        for (var i = 0; i < EmptyFiles; i++)
        {
            var path = Path.Combine(mixedSource, $"フォルダー {i % 100:D3}", $"空ファイル {i:D5}.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i));
        }
        var random = new Random(20261113);
        var quotient = OneGiB / DataFiles;
        var remainder = OneGiB % DataFiles;
        for (var i = 0; i < DataFiles; i++)
        {
            var bytes = new byte[checked((int)(quotient + (i < remainder ? 1 : 0)))];
            random.NextBytes(bytes);
            var path = DataPath(mixedSource, i);
            // WriteAllBytes writes real data; neither SetLength nor sparse allocation is used.
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i));
            if ((i + 1) % 100 == 0) Console.WriteLine($"  generated binary data {i + 1}/{DataFiles}");
        }
        Directory.CreateDirectory(Path.Combine(mixedSource, "空のフォルダー", "さらに空"));
        var files = Directory.GetFiles(mixedSource, "*", SearchOption.AllDirectories);
        var byteCount = files.Sum(p => new FileInfo(p).Length);
        Assert(files.Length == TotalFiles, "Mixed source file count.");
        Assert(byteCount == OneGiB, "Mixed source exact byte count.");
        Assert(files.All(p => (File.GetAttributes(p) & FileAttributes.SparseFile) == 0), "Dataset must not be sparse.");
        return Task.FromResult(new { Files = files.Length, EmptyFiles, DataFiles, Bytes = byteCount });
    });
    await CopyAndVerify("mixed", mixedSource, mixedDestination, TotalFiles, OneGiB);

    var retained = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
    long deltaBytes = 0;
    await Phase("mixed-mutate-same-size-and-rename", () =>
    {
        var random = new Random(20261114);
        for (var i = 0; i < 20; i++)
        {
            var path = DataPath(mixedSource, i);
            var oldTimestamp = File.GetLastWriteTimeUtc(path);
            var bytes = new byte[4096];
            random.NextBytes(bytes);
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            File.SetLastWriteTimeUtc(path, i < 10 ? oldTimestamp.AddMinutes(1) : oldTimestamp.AddDays(-1));
            deltaBytes += new FileInfo(path).Length;
        }
        for (var i = 20; i < 30; i++)
        {
            var path = DataPath(mixedSource, i);
            var relative = Path.GetRelativePath(mixedSource, path);
            retained.Add(relative, Snapshot(path));
            File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, $"追加データ {i:D4}.bin"));
            deltaBytes += retained[relative].Length;
        }
        var files = Directory.GetFiles(mixedSource, "*", SearchOption.AllDirectories);
        Assert(files.Length == TotalFiles && files.Sum(p => new FileInfo(p).Length) == OneGiB,
            "After mutation source remains exactly 10,000 files and 1 GiB.");
        return Task.FromResult(new { Updates = 20, Additions = 10, RemovedFromSource = 10, Files = files.Length, Bytes = OneGiB });
    });
    var deltaPlan = await Phase("mixed-delta-preview", async () =>
    {
        var plan = await engine.CreatePlanAsync(new BackupRequest(mixedSource, mixedDestination));
        Assert(plan.Files.Count == 30, "Delta planned file count.");
        Assert(plan.Files.Count(p => p.Kind == BackupChangeKind.Updated) == 20, "Delta updated count.");
        Assert(plan.Files.Count(p => p.Kind == BackupChangeKind.New) == 10, "Delta new count.");
        Assert(plan.UnchangedFiles == 9970, "Delta unchanged count.");
        Assert(plan.TotalBytes == deltaBytes && plan.SkippedPaths.Count == 0, "Delta bytes/skipped.");
        return plan;
    }, p => new { Files = p.Files.Count, Updated = 20, New = 10, p.UnchangedFiles, p.TotalBytes, Skipped = p.SkippedPaths.Count });
    await Phase("mixed-delta-copy", async () =>
    {
        var result = await engine.ExecuteAsync(deltaPlan, new ConsoleProgress("mixed-delta"));
        Assert(result.Success && result.CopiedFiles == 30 && result.BytesCopied == deltaBytes, "Delta result: " + Describe(result));
        return new { result.CopiedFiles, result.BytesCopied, Errors = result.Errors.Count, result.IsCanceled };
    });
    await Phase("mixed-delta-verify-all-hashes-and-retained-deletions", () =>
    {
        var inventory = VerifyTrees(mixedSource, mixedDestination, retained);
        Assert(inventory.SourceFiles == 10000 && inventory.DestinationFiles == 10010, "Retained deletion count.");
        Assert(inventory.SourceBytes == OneGiB, "Source exact byte count after delta.");
        AssertNoStaging(scratchRoot);
        return Task.FromResult(inventory);
    });
    await Phase("mixed-final-no-change", async () =>
    {
        var plan = await engine.CreatePlanAsync(new BackupRequest(mixedSource, mixedDestination));
        Assert(plan.Files.Count == 0 && plan.UnchangedFiles == 10000 && plan.SkippedPaths.Count == 0, "Final unchanged preview.");
        var result = await engine.ExecuteAsync(plan);
        Assert(result.Success && result.CopiedFiles == 0 && result.BytesCopied == 0, "Final unchanged execute.");
        return new { plan.UnchangedFiles, result.CopiedFiles, Errors = result.Errors.Count };
    });
    report.Passed = true;
}
catch (Exception ex)
{
    failed = true;
    report.Error = ex.ToString();
    Console.Error.WriteLine("FAIL " + ex);
}
finally
{
    var cleanupWatch = Stopwatch.StartNew();
    try
    {
        var fullTarget = Path.GetFullPath(scratchRoot);
        var fullQa = Path.GetFullPath(qaRoot);
        Assert(fullTarget == Path.Combine(fullQa, runName) &&
            fullTarget.StartsWith(fullQa + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(fullTarget).StartsWith("load-engine-", StringComparison.Ordinal), "Cleanup target escaped its dedicated scratch root.");
        Assert(!Directory.Exists(fullQa) || (File.GetAttributes(fullQa) & FileAttributes.ReparsePoint) == 0,
            "Cleanup refuses a reparse-point scratch parent.");
        if (Directory.Exists(fullTarget))
        {
            // Reject reparse points before recursive deletion to prevent following any aliases.
            CheckNoReparsePoints(fullTarget);
            Directory.Delete(fullTarget, recursive: true);
        }
        report.CleanupConfirmed = !Directory.Exists(fullTarget) && !File.Exists(fullTarget);
        Assert(report.CleanupConfirmed, "Scratch root still exists after cleanup.");
        report.RemainingPaths = [];
        Console.WriteLine($"CLEANUP PASS: dedicated input and backup trees removed ({cleanupWatch.Elapsed.TotalSeconds:F3}s)");
    }
    catch (Exception ex)
    {
        failed = true;
        report.Passed = false;
        report.CleanupError = ex.ToString();
        report.RemainingPaths = [scratchRoot];
        Console.Error.WriteLine("CLEANUP FAIL " + ex);
    }
    report.CleanupSeconds = cleanupWatch.Elapsed.TotalSeconds;
    report.TotalSeconds = timer.Elapsed.TotalSeconds;
    report.FinishedUtc = DateTime.UtcNow;
    var artifacts = Path.Combine(workspace, "artifacts");
    Directory.CreateDirectory(artifacts);
    File.WriteAllText(Path.Combine(artifacts, "load-validation.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    var markdown = new StringBuilder("# Real-file load validation\n\n");
    markdown.AppendLine($"Status: **{(report.Passed ? "PASS" : "FAIL")}**. Cleanup confirmed: **{report.CleanupConfirmed}**.");
    markdown.AppendLine("\nDatasets: 10,000 empty files; 10,000 mixed files (9,000 empty + 1,000 random binary) totaling exactly 1,073,741,824 bytes (1 GiB). All data were physically written with ordinary file writes, not sparse files.");
    markdown.AppendLine("\n| Phase | Seconds | Details |\n| --- | ---: | --- |");
    foreach (var phase in report.Phases)
        markdown.AppendLine($"| {phase.Name} | {phase.Seconds:F3} | {JsonSerializer.Serialize(phase.Details).Replace("|", "\\|")} |");
    markdown.AppendLine($"\nCleanup: {report.CleanupSeconds:F3} s. Total: {report.TotalSeconds:F3} s.");
    markdown.AppendLine("\nThe production backup engine was exercised. Source/backup file counts, byte counts, SHA-256 hashes, last-write UTC timestamps and directory sets were compared. Changed files with both newer and older timestamps were tested. Deleted source files were retained in backup. No staging files were left after operations. GUI verification is a separate test.");
    if (report.Error is not null) markdown.AppendLine("\nError: " + report.Error);
    if (report.CleanupError is not null) markdown.AppendLine("\nCleanup error: " + report.CleanupError);
    File.WriteAllText(Path.Combine(artifacts, "load-validation.md"), markdown.ToString());
}
return failed ? 1 : 0;

async Task CopyAndVerify(string label, string source, string destination, int expectedFiles, long expectedBytes)
{
    var plan = await Phase(label + "-initial-preview", async () =>
    {
        Assert(!Directory.Exists(destination), "Destination must be absent before preview.");
        var preview = await engine.CreatePlanAsync(new BackupRequest(source, destination));
        Assert(!Directory.Exists(destination), "Preview must not create the destination.");
        Assert(preview.Files.Count == expectedFiles && preview.UnchangedFiles == 0 && preview.TotalBytes == expectedBytes && preview.SkippedPaths.Count == 0, "Initial preview inventory.");
        return preview;
    }, p => new { Files = p.Files.Count, p.UnchangedFiles, p.TotalBytes, Skipped = p.SkippedPaths.Count });
    await Phase(label + "-initial-copy", async () =>
    {
        var result = await engine.ExecuteAsync(plan, new ConsoleProgress(label));
        Assert(result.Success && result.CopiedFiles == expectedFiles && result.BytesCopied == expectedBytes, "Initial backup: " + Describe(result));
        return new { result.CopiedFiles, result.BytesCopied, Errors = result.Errors.Count, result.IsCanceled };
    });
    await Phase(label + "-verify-all-file-hashes-metadata-and-directories", () =>
    {
        var inventory = VerifyTrees(source, destination, new Dictionary<string, FileSnapshot>());
        Assert(inventory.SourceFiles == expectedFiles && inventory.DestinationFiles == expectedFiles && inventory.SourceBytes == expectedBytes && inventory.DestinationBytes == expectedBytes, "Initial source/destination inventory.");
        AssertNoStaging(scratchRoot);
        return Task.FromResult(inventory);
    });
    await Phase(label + "-unchanged-preview-and-execute", async () =>
    {
        var preview = await engine.CreatePlanAsync(new BackupRequest(source, destination));
        Assert(preview.Files.Count == 0 && preview.UnchangedFiles == expectedFiles && preview.SkippedPaths.Count == 0, "Unchanged preview.");
        var result = await engine.ExecuteAsync(preview);
        Assert(result.Success && result.CopiedFiles == 0 && result.BytesCopied == 0, "Unchanged backup: " + Describe(result));
        return new { preview.UnchangedFiles, result.CopiedFiles, result.BytesCopied, Errors = result.Errors.Count };
    });
}

async Task<T> Phase<T>(string name, Func<Task<T>> operation, Func<T, object>? describe = null)
{
    Console.WriteLine($"START {name}");
    var watch = Stopwatch.StartNew();
    var result = await operation();
    var details = describe is null ? (object?)result : describe(result);
    report.Phases.Add(new PhaseResult(name, watch.Elapsed.TotalSeconds, details));
    Console.WriteLine($"PASS {name} ({watch.Elapsed.TotalSeconds:F3}s) {JsonSerializer.Serialize(details)}");
    return result;
}

static string DataPath(string root, int index) => Path.Combine(root, $"フォルダー {index % 100:D3}", $"データ {index:D4}.bin");
static string Describe(BackupResult r) => $"Success={r.Success}, copied={r.CopiedFiles}, bytes={r.BytesCopied}, errors={string.Join(';', r.Errors)}";
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static FileSnapshot Snapshot(string path)
{
    var info = new FileInfo(path);
    using var file = File.OpenRead(path);
    return new FileSnapshot(info.Length, info.LastWriteTimeUtc, Convert.ToHexString(SHA256.HashData(file)));
}
static InventoryResult VerifyTrees(string source, string destination, IReadOnlyDictionary<string, FileSnapshot> retained)
{
    var sourcePaths = Directory.GetFiles(source, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
    var destinationPaths = Directory.GetFiles(destination, "*", SearchOption.AllDirectories);
    var sourceRelative = sourcePaths.Select(p => Path.GetRelativePath(source, p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var destinationRelative = destinationPaths.Select(p => Path.GetRelativePath(destination, p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var expectedDestination = sourceRelative.Concat(retained.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
    Assert(destinationRelative.SetEquals(expectedDestination), "Destination relative path set differs.");
    var sourceDirs = Directory.GetDirectories(source, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(source, p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var destinationDirs = Directory.GetDirectories(destination, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(destination, p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    Assert(sourceDirs.SetEquals(destinationDirs), "Directory sets differ, including empty folders.");
    var index = 0;
    var nonEmpty = 0;
    long sourceBytes = 0;
    using var inventoryHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (var sourcePath in sourcePaths)
    {
        var relative = Path.GetRelativePath(source, sourcePath);
        var src = Snapshot(sourcePath);
        var dst = Snapshot(Path.Combine(destination, relative));
        Assert(src == dst, "File hash/size/timestamp differs: " + relative);
        sourceBytes += src.Length;
        if (src.Length > 0) nonEmpty++;
        inventoryHash.AppendData(Encoding.UTF8.GetBytes(relative + "\t" + src.Length + "\t" + src.LastWriteTimeUtc.Ticks + "\t" + src.Sha256 + "\n"));
        if (++index % 1000 == 0) Console.WriteLine($"  verified SHA-256 and metadata {index}/{sourcePaths.Length}");
    }
    foreach (var pair in retained)
        Assert(Snapshot(Path.Combine(destination, pair.Key)) == pair.Value, "Deleted source backup was changed: " + pair.Key);
    return new InventoryResult(sourcePaths.Length, destinationPaths.Length, sourceBytes,
        destinationPaths.Sum(p => new FileInfo(p).Length), sourcePaths.Length - nonEmpty, nonEmpty,
        sourcePaths.Length + retained.Count, sourceDirs.Count, Convert.ToHexString(inventoryHash.GetHashAndReset()));
}
static void AssertNoStaging(string root) => Assert(!Directory.EnumerateFiles(root, ".manualbackup-*.tmp", SearchOption.AllDirectories).Any(), "A staging file was left behind.");
static void CheckNoReparsePoints(string root)
{
    var pending = new Stack<string>();
    pending.Push(root);
    while (pending.TryPop(out var path))
    {
        var attributes = File.GetAttributes(path);
        Assert((attributes & FileAttributes.ReparsePoint) == 0, "Cleanup refuses a reparse point: " + path);
        if ((attributes & FileAttributes.Directory) == 0) continue;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path)) pending.Push(entry);
    }
}

sealed record FileSnapshot(long Length, DateTime LastWriteTimeUtc, string Sha256);
sealed record InventoryResult(int SourceFiles, int DestinationFiles, long SourceBytes, long DestinationBytes,
    int EmptyFiles, int NonEmptyFiles, int VerifiedHashPairs, int Directories, string SourceInventorySha256);
sealed record PhaseResult(string Name, double Seconds, object? Details);
sealed class RunReport
{
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public string ScratchRoot { get; set; } = "";
    public long ExactDatasetBytes { get; set; }
    public string[] Notes { get; set; } = [];
    public bool Passed { get; set; }
    public bool CleanupConfirmed { get; set; }
    public double CleanupSeconds { get; set; }
    public double TotalSeconds { get; set; }
    public string[] RemainingPaths { get; set; } = [];
    public string? Error { get; set; }
    public string? CleanupError { get; set; }
    public List<PhaseResult> Phases { get; } = [];
}
sealed class ConsoleProgress(string label) : IProgress<BackupProgress>
{
    private long _lastTicks = Stopwatch.GetTimestamp();
    public void Report(BackupProgress value)
    {
        var ticks = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_lastTicks, ticks).TotalSeconds < 10) return;
        _lastTicks = ticks;
        Console.WriteLine($"  {label}: copied {value.CompletedFiles}/{value.TotalFiles}, {value.BytesCopied}/{value.TotalBytes} bytes");
    }
}
