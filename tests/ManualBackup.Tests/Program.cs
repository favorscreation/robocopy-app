using ManualBackup.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Paired restore keeps same-name files isolated and aggregates content differences", MultiRestoreScenarios.RoundTrip),
    ("Paired restore validates every later source and destination before any writes", MultiRestoreScenarios.StaleLaterRow),
    ("Paired restore rejects overlapping targets and cross-row source writes", MultiRestoreScenarios.Overlaps),
    ("Paired restore refuses missing sources and unreviewable rows", MultiRestoreScenarios.InvalidRows),
    ("Paired restore cancellation retains completed rows and preserves active originals", MultiRestoreScenarios.Cancellation),
    ("Paired restore acquires every destination lock before any writes", MultiRestoreScenarios.Locks),
    ("Paired restore stops later rows on copy errors and observer failures", MultiRestoreScenarios.PartialFailure),
    ("Parallel limit one retains serial copying with one actual active staging file", ParallelScenarios.SerialLimit),
    ("Parallel limit two copies two real sources simultaneously and respects its cap", ParallelScenarios.TwoSourcesAtOnce),
    ("Parallel limit four copies four real sources simultaneously and respects its cap", ParallelScenarios.FourSourcesAtOnce),
    ("Invalid source parallelism limits are rejected before writes", ParallelScenarios.InvalidLimits),
    ("Parallel coordinator holds root and every child restore lock until workers join", ParallelScenarios.ParallelLocksCoverAllTargets),
    ("Parallel cancellation joins workers, preserves originals and leaves no background writes", ParallelScenarios.ParallelCancellationJoinsWorkers),
    ("Parallel file errors preserve originals while independent workers continue", ParallelScenarios.ParallelFileFailureContinues),
    ("A failing progress observer cancels and joins all parallel workers before return", ParallelScenarios.ProgressFailureJoinsWorkers),
    ("Multiple sources preserve same-name files under isolated child folders", MultiSourceScenarios.IsolatedFolders),
    ("Multiple-source repeat copies only added and changed files", MultiSourceScenarios.ChangedOnlySecondRun),
    ("Multiple-source preview is read only and rejects a missing source", MultiSourceScenarios.PreviewReadOnlyAndMissingSource),
    ("Multiple-source child folder names are unique ignoring case", MultiSourceScenarios.DuplicateTargetNames),
    ("Multiple-source target aliases reject traversal and DOS device names", MultiSourceScenarios.InvalidTargetNames),
    ("Multiple-source requests reject duplicate and ancestor source folders", MultiSourceScenarios.DuplicateAndNestedSources),
    ("Batch destination overlap with any source is rejected before writes", MultiSourceScenarios.CrossSourceDestinationOverlap),
    ("Batch execution preflights every source before writing the first", MultiSourceScenarios.ExecuteRevalidatesAllSources),
    ("Batch execution preflights every target before writing the first", MultiSourceScenarios.ExecuteRevalidatesAllTargets),
    ("Legacy single-source blank alias retains direct destination layout", MultiSourceScenarios.LegacySingleSourceLayout),
    ("Batch progress totals and child path prefixes span every source", MultiSourceScenarios.AggregateProgress),
    ("Batch progress counts empty files across sources", MultiSourceScenarios.AggregateEmptyFileProgress),
    ("Batch cancellation preserves the original and skips remaining sources", MultiSourceScenarios.CancellationStopsRemainingSources),
    ("Batch file errors are prefixed and do not prevent other source copies", MultiSourceScenarios.FileErrorsContinueAcrossSources),
    ("Concurrent batches share a destination root lock across different child mappings", MultiSourceScenarios.ConcurrentBatchesShareRootLock),
    ("Restore round trip recovers older data and missing files while retaining destination-only files", RestoreScenarios.RoundTrip),
    ("Restore content comparison detects different bytes with identical size and timestamp", RestoreScenarios.SameMetadataDifference),
    ("Restore updates timestamp-only differences even when file contents match", RestoreScenarios.TimestampOnlyDifference),
    ("Restore rejects changed destination before writing any planned file", RestoreScenarios.DestinationChangedAfterPreview),
    ("Restore rejects a new file appearing at a previously missing destination", RestoreScenarios.NewDestinationAppeared),
    ("Restore rejects source edits even when size and timestamp are preserved", RestoreScenarios.SourceChangedAfterPreview),
    ("Restore invalidates preview when previously unchanged source or destination changes", RestoreScenarios.UnchangedFilesChangedAfterPreview),
    ("Restore preview is read only and rejects overlapping folders", RestoreScenarios.PreviewSafety),
    ("Restore cancellation keeps the existing destination and removes staging files", RestoreScenarios.Cancellation),
    ("Restore preserves a destination edit made while copying", RestoreScenarios.DestinationChangedDuringCopy),
    ("Initial backup copies Japanese paths, timestamps and empty directories", InitialCopy),
    ("Unchanged files are skipped on the next run", Unchanged),
    ("Same-size changed files and older timestamps are copied", ChangedFiles),
    ("Files removed from source remain in backup", DeletedSourcePreserved),
    ("Preview does not create the destination", PreviewIsReadOnly),
    ("Equal and overlapping paths are rejected", OverlapsRejected),
    ("Windows path aliases cannot bypass overlap checks", PathAliasesRejected),
    ("Missing source is rejected", MissingSourceRejected),
    ("Source changed after preview does not overwrite destination", StalePlanRejected),
    ("A new backup target appearing after preview is retained and reported as an error", NewTargetAppearedAfterPreview),
    ("Locked destination yields partial failure and preserves original", LockedDestination),
    ("Cancellation keeps original destination and cleans staging files", Cancellation),
    ("Concurrent backups cannot write the same destination", ConcurrentExecution),
    ("A pre-canceled preview is interrupted", PreviewCancellation),
    ("Source junctions are skipped and destination junctions are rejected", JunctionSafety),
    ("Destination parent replaced by a junction after preview is rejected", JunctionInsertedAfterPreview)
};

var failed = 0;
var skipped = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (TestSkippedException e)
    {
        skipped++;
        Console.WriteLine($"SKIP {test.Name}: {e.Message}");
    }
    catch (Exception e)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}\n{e}");
    }
}
Console.WriteLine($"{tests.Length - failed - skipped} passed, {failed} failed, {skipped} skipped ({tests.Length} tests).");
return failed == 0 ? 0 : 1;

static async Task InitialCopy()
{
    using var f = new Fixture();
    f.WriteSource("資料 日本語/見積 01.txt", "こんにちは UTF-8");
    f.WriteSource("root.txt", "first");
    Directory.CreateDirectory(Path.Combine(f.Source, "空のフォルダー"));
    var original = File.GetLastWriteTimeUtc(Path.Combine(f.Source, "資料 日本語/見積 01.txt"));
    var plan = await f.Plan();
    Equal(2, plan.Files.Count, "planned files");
    Equal(2, plan.Files.Count(x => x.Kind == BackupChangeKind.New), "new files");
    var result = await f.Engine.ExecuteAsync(plan);
    True(result.Success, Describe(result));
    Equal(2, result.CopiedFiles, "copied files");
    Equal("こんにちは UTF-8", File.ReadAllText(Path.Combine(f.Destination, "資料 日本語/見積 01.txt")), "Japanese content");
    Equal(original, File.GetLastWriteTimeUtc(Path.Combine(f.Destination, "資料 日本語/見積 01.txt")), "timestamp");
    True(Directory.Exists(Path.Combine(f.Destination, "空のフォルダー")), "empty directory copied");
}

static async Task Unchanged()
{
    using var f = new Fixture();
    f.WriteSource("a.txt", "unchanged");
    await f.Backup();
    var plan = await f.Plan();
    Equal(0, plan.Files.Count, "no planned files");
    Equal(1, plan.UnchangedFiles, "unchanged count");
    var result = await f.Engine.ExecuteAsync(plan);
    True(result.Success, Describe(result));
    Equal(0, result.CopiedFiles, "no copies");
}

static async Task ChangedFiles()
{
    using var f = new Fixture();
    f.WriteSource("later.txt", "aaaa");
    f.WriteSource("older.txt", "bbbb");
    await f.Backup();
    var original = File.GetLastWriteTimeUtc(Path.Combine(f.Source, "later.txt"));
    f.WriteSource("later.txt", "cccc");
    f.WriteSource("older.txt", "dddd");
    File.SetLastWriteTimeUtc(Path.Combine(f.Source, "later.txt"), original.AddMinutes(1));
    File.SetLastWriteTimeUtc(Path.Combine(f.Source, "older.txt"), original.AddDays(-1));
    var plan = await f.Plan();
    Equal(2, plan.Files.Count, "same-size updates detected");
    True(plan.Files.All(x => x.Kind == BackupChangeKind.Updated), "update classification");
    var result = await f.Engine.ExecuteAsync(plan);
    True(result.Success, Describe(result));
    Equal("cccc", File.ReadAllText(Path.Combine(f.Destination, "later.txt")), "later copy");
    Equal("dddd", File.ReadAllText(Path.Combine(f.Destination, "older.txt")), "older copy");
}

static async Task DeletedSourcePreserved()
{
    using var f = new Fixture();
    f.WriteSource("keep.txt", "keep me");
    await f.Backup();
    File.Delete(Path.Combine(f.Source, "keep.txt"));
    var result = await f.Engine.ExecuteAsync(await f.Plan());
    True(result.Success, Describe(result));
    Equal("keep me", File.ReadAllText(Path.Combine(f.Destination, "keep.txt")), "deleted source retained in backup");
}

static async Task PreviewIsReadOnly()
{
    using var f = new Fixture();
    f.WriteSource("nested/a.txt", "preview");
    var before = Directory.GetFileSystemEntries(f.Root).Order().ToArray();
    _ = await f.Plan();
    True(!Directory.Exists(f.Destination), "destination must not be created by preview");
    True(before.SequenceEqual(Directory.GetFileSystemEntries(f.Root).Order()), "preview must not create lock or staging files");
}

static async Task OverlapsRejected()
{
    using var f = new Fixture();
    foreach (var request in new[]
    {
        new BackupRequest(f.Source, f.Source),
        new BackupRequest(f.Source, Path.Combine(f.Source, "child")),
        new BackupRequest(f.Source, f.Root),
        new BackupRequest(f.Source + Path.DirectorySeparatorChar, f.Source),
        new BackupRequest(f.Source, Path.Combine(f.Source, "sub", ".."))
    })
        await Throws<ArgumentException>(() => f.Engine.CreatePlanAsync(request));
    // A shared lexical prefix is safe when the paths are siblings.
    var sibling = f.Source + "-sibling";
    _ = await f.Engine.CreatePlanAsync(new BackupRequest(f.Source, sibling));
    True(!Directory.Exists(sibling), "preview of sibling must be read only");
}

static async Task MissingSourceRejected()
{
    using var f = new Fixture();
    await Throws<DirectoryNotFoundException>(() => f.Engine.CreatePlanAsync(new BackupRequest(Path.Combine(f.Root, "missing"), f.Destination)));
}

static async Task PathAliasesRejected()
{
    if (!OperatingSystem.IsWindows()) throw new TestSkippedException("Windows path alias test");
    using var f = new Fixture();
    foreach (var request in new[]
    {
        new BackupRequest(f.Source, @"\\?\" + Path.Combine(f.Source, "backup")),
        new BackupRequest(@"\\?\" + f.Source, Path.Combine(f.Source, "backup")),
        new BackupRequest(f.Source, Path.Combine(f.Source + " ", "backup")),
        new BackupRequest(f.Source, Path.Combine(f.Source + ". ", "backup"))
    })
        await Throws<ArgumentException>(() => f.Engine.CreatePlanAsync(request));
}

static async Task StalePlanRejected()
{
    using var f = new Fixture();
    f.WriteSource("a.txt", "original");
    await f.Backup();
    f.WriteSource("a.txt", "planned change");
    var plan = await f.Plan();
    f.WriteSource("a.txt", "mutated after preview with another length");
    var result = await f.Engine.ExecuteAsync(plan);
    True(!result.Success && result.Errors.Count > 0, "stale source must report an error");
    Equal("original", File.ReadAllText(Path.Combine(f.Destination, "a.txt")), "original backup remains");
}

static async Task LockedDestination()
{
    using var f = new Fixture();
    f.WriteSource("locked.txt", "old");
    await f.Backup();
    f.WriteSource("locked.txt", "new content");
    f.WriteSource("unlocked.txt", "still copied");
    var plan = await f.Plan();
    using (File.Open(Path.Combine(f.Destination, "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
    {
        var result = await f.Engine.ExecuteAsync(plan);
        True(!result.Success, "locked destination cannot report success");
        True(result.Errors.Count > 0, "locked destination error reported");
        Equal(1, result.CopiedFiles, "other file still copied");
    }
    Equal("old", File.ReadAllText(Path.Combine(f.Destination, "locked.txt")), "locked original unchanged");
    Equal("still copied", File.ReadAllText(Path.Combine(f.Destination, "unlocked.txt")), "unlocked copy");
    True(!Directory.GetFiles(f.Destination, "*", SearchOption.AllDirectories).Any(x => Path.GetFileName(x).Contains(".tmp", StringComparison.OrdinalIgnoreCase)), "no staging files left");
}

static async Task NewTargetAppearedAfterPreview()
{
    using var f = new Fixture();
    f.WriteSource("new.txt", "SOURCE");
    var plan = await f.Plan();
    Equal(BackupChangeKind.New, plan.Files.Single().Kind, "planned target was absent");
    Directory.CreateDirectory(f.Destination);
    File.WriteAllText(Path.Combine(f.Destination, "new.txt"), "APPEARED AFTER PREVIEW");
    var result = await f.Engine.ExecuteAsync(plan);
    True(!result.Success && !result.IsCanceled, "appeared target produces an error");
    Equal(0, result.CopiedFiles, "appeared target was not overwritten");
    True(result.Errors.Any(x => x.RelativePath == "new.txt"), "appeared target error identifies the file");
    Equal("APPEARED AFTER PREVIEW", File.ReadAllText(Path.Combine(f.Destination, "new.txt")), "newly appeared destination retained");
    Equal("SOURCE", File.ReadAllText(Path.Combine(f.Source, "new.txt")), "source retained");
    Equal(1, Directory.GetFiles(f.Destination, "*", SearchOption.AllDirectories).Length, "staging file cleaned");
}

static async Task Cancellation()
{
    using var f = new Fixture();
    f.WriteSource("large.bin", "original");
    await f.Backup();
    var source = Path.Combine(f.Source, "large.bin");
    using (var file = File.Open(source, FileMode.Create, FileAccess.Write, FileShare.None))
        file.SetLength(32 * 1024 * 1024);
    var plan = await f.Plan();
    using var ct = new CancellationTokenSource();
    var progressObserved = false;
    var progress = new InlineProgress<BackupProgress>(p =>
    {
        if (p.BytesCopied > 0 && p.CompletedFiles == 0)
        {
            progressObserved = true;
            ct.Cancel();
        }
    });
    var result = await f.Engine.ExecuteAsync(plan, progress, ct.Token);
    True(progressObserved, "mid-file copy progress must be reported");
    True(result.IsCanceled, "canceled status");
    True(!result.Success, "canceled run cannot report success");
    Equal("original", File.ReadAllText(Path.Combine(f.Destination, "large.bin")), "original survives mid-file cancellation");
    Equal(1, Directory.GetFiles(f.Destination, "*", SearchOption.AllDirectories).Length, "only original file remains");
}

static async Task PreviewCancellation()
{
    using var f = new Fixture();
    using var ct = new CancellationTokenSource();
    ct.Cancel();
    await Throws<OperationCanceledException>(() => f.Engine.CreatePlanAsync(new BackupRequest(f.Source, f.Destination), ct.Token));
    True(!Directory.Exists(f.Destination), "canceled preview does not write destination");
}

static async Task ConcurrentExecution()
{
    using var f = new Fixture();
    using (var file = File.Open(Path.Combine(f.Source, "large.bin"), FileMode.Create, FileAccess.Write, FileShare.None))
        file.SetLength(2 * 1024 * 1024);
    var plan = await f.Plan();
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var first = f.Engine.ExecuteAsync(plan, new InlineProgress<BackupProgress>(p =>
    {
        if (p.BytesCopied > 0 && p.CompletedFiles == 0)
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("Concurrent test did not release the first copy.");
        }
    }));
    try
    {
        True(entered.Wait(TimeSpan.FromSeconds(10)), "first backup entered the copy loop");
        await Throws<IOException>(() => f.Engine.ExecuteAsync(plan));
    }
    finally { release.Set(); }
    var result = await first;
    True(result.Success, Describe(result));
}

static async Task JunctionSafety()
{
    using var f = new Fixture();
    var external = Path.Combine(f.Root, "external");
    Directory.CreateDirectory(external);
    File.WriteAllText(Path.Combine(external, "sensitive.txt"), "outside");
    var link = Path.Combine(f.Source, "junction");
    CreateJunction(link, external);
    try
    {
        var plan = await f.Plan();
        True(plan.Files.All(x => !x.RelativePath.Contains("sensitive.txt")), "source junction must not be followed");
        True(plan.SkippedPaths.Count > 0, "source junction skip must be visible");
        await Throws<IOException>(() => f.Engine.CreatePlanAsync(new BackupRequest(link, f.Destination)));
        await Throws<ArgumentException>(() => f.Engine.CreatePlanAsync(new BackupRequest(f.Source, Path.Combine(link, "backup"))));
        var destinationLink = Path.Combine(f.Root, "destination-link");
        CreateJunction(destinationLink, external);
        try
        {
            await Throws<IOException>(() => f.Engine.CreatePlanAsync(new BackupRequest(f.Source, destinationLink)));
        }
        finally { Directory.Delete(destinationLink); }
    }
    finally { Directory.Delete(link); }
}

static async Task JunctionInsertedAfterPreview()
{
    using var f = new Fixture();
    f.WriteSource("nested/a.txt", "new");
    Directory.CreateDirectory(f.Destination);
    var plan = await f.Plan();
    var external = Path.Combine(f.Root, "external");
    Directory.CreateDirectory(external);
    File.WriteAllText(Path.Combine(external, "a.txt"), "protected");
    var link = Path.Combine(f.Destination, "nested");
    CreateJunction(link, external);
    try
    {
        try
        {
            var result = await f.Engine.ExecuteAsync(plan);
            True(!result.Success && result.Errors.Count > 0, "new destination junction must prevent successful copy");
        }
        catch (ArgumentException) { }
        Equal("protected", File.ReadAllText(Path.Combine(external, "a.txt")), "no write through replaced destination directory");
    }
    finally { Directory.Delete(link); }
}

static void CreateJunction(string link, string target)
{
    if (!OperatingSystem.IsWindows()) throw new TestSkippedException("Windows junction test");
    var info = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    info.ArgumentList.Add("/d");
    info.ArgumentList.Add("/c");
    info.ArgumentList.Add("mklink");
    info.ArgumentList.Add("/J");
    info.ArgumentList.Add(link);
    info.ArgumentList.Add(target);
    using var process = System.Diagnostics.Process.Start(info)!;
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0) throw new TestSkippedException($"Junction creation unavailable: {stdout}{stderr}");
}

static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

static void True(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{message}: expected {expected}, actual {actual}");
}

static string Describe(BackupResult result) => string.Join("; ", result.Errors.Select(x => x.RelativePath + ": " + x.Message));

sealed class Fixture : IDisposable
{
    private readonly string _prefix = Path.Combine(Path.GetTempPath(), "ManualBackup.Tests-");
    public string Root { get; }
    public string Source => Path.Combine(Root, "source");
    public string Destination => Path.Combine(Root, "backup");
    public BackupEngine Engine { get; } = new();

    public Fixture()
    {
        Root = Path.GetFullPath(_prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Source);
    }

    public void WriteSource(string relative, string value)
    {
        var path = Path.Combine(Source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }

    public Task<BackupPlan> Plan() => Engine.CreatePlanAsync(new BackupRequest(Source, Destination));
    public async Task Backup()
    {
        var result = await Engine.ExecuteAsync(await Plan());
        if (!result.Success) throw new Exception("Fixture backup failed: " + string.Join("; ", result.Errors.Select(x => x.Message)));
    }

    public void Dispose()
    {
        // Delete only the unique temporary directory created by this instance.
        var full = Path.GetFullPath(Root);
        if (!full.StartsWith(Path.GetFullPath(_prefix), StringComparison.OrdinalIgnoreCase) || full == Path.GetFullPath(_prefix))
            throw new InvalidOperationException("Unsafe test cleanup path.");
        try { Directory.Delete(full, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}

sealed class TestSkippedException(string message) : Exception(message);
