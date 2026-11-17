using ManualBackup.Core;

internal static class MultiSourceScenarios
{
    private static readonly DateTime Stamp = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    public static async Task IsolatedFolders()
    {
        using var f = new Fixture();
        var second = MakeSecond(f);
        Write(f.Source, "same.txt", "ACCOUNTING");
        Write(second, "same.txt", "PERSONNEL");
        Write(second, "子フォルダー/日本語.txt", "日本語の内容");
        Directory.CreateDirectory(Path.Combine(f.Source, "empty-folder"));
        var engine = new MultiBackupEngine();
        var plan = await engine.CreatePlanAsync(Request(f, second, "会計", "人事"));
        Equal(2, plan.Entries.Count, "two source plans");
        Equal(3, plan.Files.Count, "all changed files combined");
        True(plan.Files.All(x => x.RelativePath.StartsWith("会計" + Path.DirectorySeparatorChar, StringComparison.Ordinal) || x.RelativePath.StartsWith("人事" + Path.DirectorySeparatorChar, StringComparison.Ordinal)), "display paths identify their source child");
        Equal(plan.Files.Sum(x => x.Length), plan.TotalBytes, "aggregate byte count");
        var result = await engine.ExecuteAsync(plan);
        Success(result);
        Equal(3, result.CopiedFiles, "all files copied");
        Equal("ACCOUNTING", Read(f.Destination, "会計/same.txt"), "first same-name file isolated");
        Equal("PERSONNEL", Read(f.Destination, "人事/same.txt"), "second same-name file isolated");
        Equal("日本語の内容", Read(f.Destination, "人事/子フォルダー/日本語.txt"), "nested Japanese path");
        True(Directory.Exists(Path.Combine(f.Destination, "会計/empty-folder")), "empty directory copied under the correct child");
        True(!File.Exists(Path.Combine(f.Destination, "same.txt")), "sources are not merged into backup root");
        Equal("ACCOUNTING", Read(f.Source, "same.txt"), "first source untouched");
        Equal("PERSONNEL", Read(second, "same.txt"), "second source untouched");
    }

    public static async Task ChangedOnlySecondRun()
    {
        using var f = new Fixture();
        var second = MakeSecond(f);
        Write(f.Source, "same.txt", "AAAA");
        Write(second, "same.txt", "BBBB");
        Write(second, "removed.txt", "KEEP");
        var request = Request(f, second);
        var engine = new MultiBackupEngine();
        Success(await engine.ExecuteAsync(await engine.CreatePlanAsync(request)));
        var unchanged = await engine.CreatePlanAsync(request);
        Equal(0, unchanged.Files.Count, "second run has no copies");
        Equal(3, unchanged.UnchangedFiles, "unchanged count spans sources");
        Success(await engine.ExecuteAsync(unchanged));
        Write(second, "same.txt", "CCCC", Stamp.AddDays(-1));
        Write(f.Source, "new.txt", "NEW");
        File.Delete(Path.Combine(second, "removed.txt"));
        var changes = await engine.CreatePlanAsync(request);
        Equal(2, changes.Files.Count, "one update plus one new file");
        Equal(1, changes.Files.Count(x => x.Kind == BackupChangeKind.New), "aggregate new count");
        Equal(1, changes.Files.Count(x => x.Kind == BackupChangeKind.Updated), "aggregate updated count");
        Equal(1, changes.UnchangedFiles, "one unchanged source file");
        var result = await engine.ExecuteAsync(changes);
        Success(result);
        Equal(2, result.CopiedFiles, "only new and changed files copied");
        Equal("AAAA", Read(f.Destination, "first/same.txt"), "unchanged first source retained");
        Equal("CCCC", Read(f.Destination, "second/same.txt"), "same-size older update copied");
        Equal("KEEP", Read(f.Destination, "second/removed.txt"), "source deletion does not delete backup");
    }

    public static async Task PreviewReadOnlyAndMissingSource()
    {
        using var f = new Fixture();
        var second = MakeSecond(f);
        Write(f.Source, "first.txt", "FIRST");
        Write(second, "second.txt", "SECOND");
        var engine = new MultiBackupEngine();
        _ = await engine.CreatePlanAsync(Request(f, second));
        True(!Directory.Exists(f.Destination), "batch preview does not create destination root or children");
        var missing = Path.Combine(f.Root, "missing-source");
        await Throws<DirectoryNotFoundException>(() => engine.CreatePlanAsync(Request(f, missing)));
        True(!Directory.Exists(f.Destination), "invalid second source preview makes no first-source writes");
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        await Throws<OperationCanceledException>(() => engine.CreatePlanAsync(Request(f, second), ct.Token));
        True(!Directory.Exists(f.Destination), "canceled batch preview remains read only");
    }

    public static async Task DuplicateTargetNames()
    {
        using var f = new Fixture();
        var second = MakeSecond(f);
        var engine = new MultiBackupEngine();
        await Throws<ArgumentException>(() => engine.CreatePlanAsync(Request(f, second, "Reports", "reports")));
        await Throws<ArgumentException>(() => engine.CreatePlanAsync(Request(f, second, "資料", "資料")));
        True(!Directory.Exists(f.Destination), "duplicate child names are rejected before writes");
    }

    public static async Task InvalidTargetNames()
    {
        using var f = new Fixture();
        var second = MakeSecond(f);
        var engine = new MultiBackupEngine();
        await Throws<ArgumentException>(() => engine.CreatePlanAsync(new MultiBackupRequest([], f.Destination)));
        foreach (var alias in new[] { "", ".", "..", "../escape", @"nested\child", "nested/child", "C:escape", @"C:\escape", "CON", "con.txt", "NUL", "AUX.json", "COM1", "LPT9.log", "COM¹", "COM².txt", "COM³", "LPT¹", "LPT².txt", "LPT³", "LONGFO~1", "ABCD~123", "ABCDEF~1.txt", "trailing.", "trailing ", "bad:name", "bad*name" })
            await Throws<ArgumentException>(() => engine.CreatePlanAsync(Request(f, second, "safe", alias)));
        await Throws<ArgumentException>(() => engine.CreatePlanAsync(Request(f, second, "", "safe")));
        True(!Directory.Exists(f.Destination), "invalid alias validation is read only");
    }

    public static async Task DuplicateAndNestedSources()
    {
        using var f = new Fixture();
        var engine = new MultiBackupEngine();
        foreach (var duplicate in new[] { f.Source, f.Source.ToUpperInvariant(), Path.Combine(f.Source, "child", "..") })
            await Throws<ArgumentException>(() => engine.CreatePlanAsync(Request(f, duplicate)));
        var child = Path.Combine(f.Source, "child");
        Directory.CreateDirectory(child);
        await Throws<ArgumentException>(() => engine.CreatePlanAsync(Request(f, child)));
        var reversed = new MultiBackupRequest([new BackupSource(child, "child"), new BackupSource(f.Source, "parent")], f.Destination);
        await Throws<ArgumentException>(() => engine.CreatePlanAsync(reversed));
        True(!Directory.Exists(f.Destination), "duplicate and nested sources are rejected before writes");
    }

    public static async Task CrossSourceDestinationOverlap()
    {
        using var f = new Fixture();
        var second = MakeSecond(f);
        Write(f.Source, "first.txt", "FIRST");
        Write(second, "second.txt", "SECOND");
        var engine = new MultiBackupEngine();
        // The first mapping alone would be valid, but the root overlaps source two.
        var insideSecond = Path.Combine(second, "backups");
        var request = new MultiBackupRequest([new BackupSource(f.Source, "first"), new BackupSource(second, "second")], insideSecond);
        await Throws<ArgumentException>(() => engine.CreatePlanAsync(request));
        True(!Directory.Exists(insideSecond), "cross-source overlap is rejected without creating backup tree");
        var sourceInsideDestination = new MultiBackupRequest([new BackupSource(f.Source, "first"), new BackupSource(second, "second")], f.Root);
        await Throws<ArgumentException>(() => engine.CreatePlanAsync(sourceInsideDestination));
        Equal("FIRST", Read(f.Source, "first.txt"), "first source not modified");
        Equal("SECOND", Read(second, "second.txt"), "second source not modified");
    }

    public static async Task ExecuteRevalidatesAllSources()
    {
        using var f = new Fixture();
        var second = MakeSecond(f);
        Write(f.Source, "first.txt", "FIRST");
        var engine = new MultiBackupEngine();
        var plan = await engine.CreatePlanAsync(Request(f, second));
        Directory.Delete(second); // This known test-owned directory is empty.
        await Throws<DirectoryNotFoundException>(() => engine.ExecuteAsync(plan));
        True(!Directory.Exists(f.Destination), "missing source two must be detected before any source one output is created");
    }

    public static async Task ExecuteRevalidatesAllTargets()
    {
        using var f = new Fixture();
        var second = MakeSecond(f);
        Write(f.Source, "first.txt", "FIRST");
        Write(second, "second.txt", "SECOND");
        var engine = new MultiBackupEngine();
        var plan = await engine.CreatePlanAsync(Request(f, second));
        Directory.CreateDirectory(f.Destination);
        File.WriteAllText(Path.Combine(f.Destination, "second"), "BLOCKING FILE");
        await Throws<ArgumentException>(() => engine.ExecuteAsync(plan));
        True(!Directory.Exists(Path.Combine(f.Destination, "first")), "invalid target two is detected before any source one output");
        Equal("BLOCKING FILE", Read(f.Destination, "second"), "blocking destination file retained");
    }

    public static async Task LegacySingleSourceLayout()
    {
        using var f = new Fixture();
        Write(f.Source, "nested/original.txt", "LEGACY");
        var engine = new MultiBackupEngine();
        var request = new MultiBackupRequest([new BackupSource(f.Source, "")], f.Destination);
        var plan = await engine.CreatePlanAsync(request);
        Equal(Path.Combine("nested", "original.txt"), plan.Files.Single().RelativePath, "legacy relative path has no extra prefix");
        var result = await engine.ExecuteAsync(plan);
        Success(result);
        Equal("LEGACY", Read(f.Destination, "nested/original.txt"), "legacy direct destination layout retained");
        True(!Directory.Exists(Path.Combine(f.Destination, "source")), "no extra source-name folder inserted");
        Equal(0, (await engine.CreatePlanAsync(request)).Files.Count, "legacy repeat skips unchanged file");
    }

    public static async Task AggregateProgress()
    {
        using var f = new Fixture();
        var second = MakeSecond(f);
        Write(f.Source, "a.txt", "FIRST");
        Write(f.Source, "empty.txt", "");
        Write(second, "a.txt", "SECOND");
        var engine = new MultiBackupEngine();
        var plan = await engine.CreatePlanAsync(Request(f, second));
        var updates = new List<BackupProgress>();
        var result = await engine.ExecuteAsync(plan, new InlineProgress<BackupProgress>(updates.Add));
        Success(result);
        True(updates.Count > 0, "batch progress emitted");
        True(updates.All(p => p.TotalFiles == 3 && p.TotalBytes == plan.TotalBytes), "progress totals cover all sources including empty files");
        True(updates.All(p => p.RelativePath.StartsWith("first" + Path.DirectorySeparatorChar, StringComparison.Ordinal) || p.RelativePath.StartsWith("second" + Path.DirectorySeparatorChar, StringComparison.Ordinal)), "progress paths identify source children");
        for (var i = 1; i < updates.Count; i++)
        {
            True(updates[i].CompletedFiles >= updates[i - 1].CompletedFiles, "completed-file progress does not reset per source");
            True(updates[i].BytesCopied >= updates[i - 1].BytesCopied, "byte progress does not reset per source");
        }
        Equal(3, updates[^1].CompletedFiles, "final aggregate completed files");
        Equal(plan.TotalBytes, updates[^1].BytesCopied, "final aggregate bytes");
        Equal(plan.TotalBytes, result.BytesCopied, "result byte count");
    }

    public static async Task AggregateEmptyFileProgress()
    {
        using var f = new Fixture();
        var second = MakeSecond(f);
        Write(f.Source, "empty.txt", "");
        Write(second, "empty.txt", "");
        var engine = new MultiBackupEngine();
        var plan = await engine.CreatePlanAsync(Request(f, second));
        var updates = new List<BackupProgress>();
        var result = await engine.ExecuteAsync(plan, new InlineProgress<BackupProgress>(updates.Add));
        Success(result);
        Equal(2, result.CopiedFiles, "empty files copied across sources");
        Equal(0L, result.BytesCopied, "zero bytes copied");
        True(updates.Count >= 2, "each empty file reports completion");
        Equal(2, updates[^1].CompletedFiles, "all empty files completed");
        Equal(2, updates[^1].TotalFiles, "aggregate empty-file total");
        Equal(0L, updates[^1].TotalBytes, "aggregate empty byte total");
    }

    public static async Task CancellationStopsRemainingSources()
    {
        using var f = new Fixture();
        var second = MakeSecond(f);
        Directory.CreateDirectory(Path.Combine(f.Destination, "first"));
        Write(Path.Combine(f.Destination, "first"), "large.bin", "ORIGINAL");
        using (var file = File.Open(Path.Combine(f.Source, "large.bin"), FileMode.Create, FileAccess.Write, FileShare.None))
            file.SetLength(4 * 1024 * 1024);
        Write(second, "unvisited.txt", "MUST NOT COPY");
        var engine = new MultiBackupEngine();
        var plan = await engine.CreatePlanAsync(Request(f, second));
        using var ct = new CancellationTokenSource();
        var progressObserved = false;
        var result = await engine.ExecuteAsync(plan, new InlineProgress<BackupProgress>(p =>
        {
            if (p.BytesCopied > 0 && p.CompletedFiles == 0)
            {
                progressObserved = true;
                ct.Cancel();
            }
        }), ct.Token);
        True(progressObserved, "first source emitted mid-file progress");
        True(result.IsCanceled && !result.Success, "batch returns canceled status");
        Equal(0, result.CopiedFiles, "interrupted staging copy is not counted as copied");
        Equal("ORIGINAL", Read(f.Destination, "first/large.bin"), "original target survives cancellation");
        True(!Directory.Exists(Path.Combine(f.Destination, "second")), "remaining source is not executed after cancel");
        Equal(1, Directory.GetFiles(f.Destination, "*", SearchOption.AllDirectories).Length, "staging files cleaned after cancellation");
    }

    public static async Task FileErrorsContinueAcrossSources()
    {
        using var f = new Fixture();
        var second = MakeSecond(f);
        Write(f.Source, "locked.txt", "NEW CONTENT");
        Write(f.Source, "unlocked.txt", "OK");
        Write(second, "same.txt", "SOURCE TWO");
        Write(f.Destination, "first/locked.txt", "OLD");
        var engine = new MultiBackupEngine();
        var plan = await engine.CreatePlanAsync(Request(f, second));
        BackupResult result;
        using (File.Open(Path.Combine(f.Destination, "first/locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            result = await engine.ExecuteAsync(plan);
        True(!result.Success && !result.IsCanceled, "partial error is not success or cancellation");
        Equal(2, result.CopiedFiles, "unlocked files in both sources still copied");
        Equal(1, result.Errors.Count, "locked target reported once");
        Equal(Path.Combine("first", "locked.txt"), result.Errors.Single().RelativePath, "error identifies its source child");
        Equal("OLD", Read(f.Destination, "first/locked.txt"), "locked original remains intact");
        Equal("OK", Read(f.Destination, "first/unlocked.txt"), "unlocked first-source copy");
        Equal("SOURCE TWO", Read(f.Destination, "second/same.txt"), "second source runs despite earlier file error");
        True(!Directory.GetFiles(f.Destination, "*.tmp", SearchOption.AllDirectories).Any(), "staging files cleaned after error");
    }

    public static async Task ConcurrentBatchesShareRootLock()
    {
        using var f = new Fixture();
        var second = MakeSecond(f);
        using (var file = File.Open(Path.Combine(f.Source, "large.bin"), FileMode.Create, FileAccess.Write, FileShare.None))
            file.SetLength(2 * 1024 * 1024);
        var engine = new MultiBackupEngine();
        var firstPlan = await engine.CreatePlanAsync(Request(f, second));
        var secondPlan = await engine.CreatePlanAsync(Request(f, second, "other-first", "other-second"));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = engine.ExecuteAsync(firstPlan, new InlineProgress<BackupProgress>(p =>
        {
            if (p.BytesCopied > 0 && p.CompletedFiles == 0)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Concurrent batch test did not release the first copy.");
            }
        }));
        try
        {
            True(entered.Wait(TimeSpan.FromSeconds(10)), "first batch reached the copy loop");
            await Throws<IOException>(() => engine.ExecuteAsync(secondPlan));
        }
        finally { release.Set(); }
        Success(await first);
        True(!Directory.Exists(Path.Combine(f.Destination, "other-first")), "second batch did not write a different child while root was locked");
    }

    private static string MakeSecond(Fixture f)
    {
        var path = Path.Combine(f.Root, "source-two");
        Directory.CreateDirectory(path);
        return path;
    }

    private static MultiBackupRequest Request(Fixture f, string second, string firstAlias = "first", string secondAlias = "second") =>
        new([new BackupSource(f.Source, firstAlias), new BackupSource(second, secondAlias)], f.Destination);

    private static void Write(string root, string relative, string content, DateTime? stamp = null)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, stamp ?? Stamp);
    }

    private static string Read(string root, string relative) => File.ReadAllText(Path.Combine(root, relative));
    private static void Success(BackupResult result) => True(result.Success, string.Join("; ", result.Errors.Select(x => x.RelativePath + ": " + x.Message)));
    private static void True(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{message}: expected {expected}, actual {actual}");
    }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
