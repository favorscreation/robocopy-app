using ManualBackup.Core;

internal static class MultiRestoreScenarios
{
    private static readonly DateTime Stamp = new(2026, 1, 2, 3, 4, 6, DateTimeKind.Utc);
    private static readonly MultiRestoreEngine Engine = new();

    public static async Task RoundTrip()
    {
        using var f = new Fixture();
        var pairs = Pairs(f);
        Write(pairs[0].SourcePath, "same.txt", "FIRST");
        Write(pairs[1].SourcePath, "same.txt", "SNAP");
        Write(pairs[1].DestinationPath, "same.txt", "EDIT");
        Write(pairs[1].SourcePath, "unchanged.txt", "SAME");
        Write(pairs[1].DestinationPath, "unchanged.txt", "SAME");
        Write(pairs[1].DestinationPath, "keep.txt", "KEEP");
        Directory.CreateDirectory(Path.Combine(pairs[0].SourcePath, "空フォルダー"));
        var plan = await Engine.CreatePlanAsync(pairs);
        Check(!Directory.Exists(pairs[0].DestinationPath), "preview must be read only");
        Check(plan.Files.Count == 2 && plan.UnchangedFiles == 1, "batch totals");
        Check(plan.Files.Select(file => file.RelativePath).SequenceEqual(new[] { "[1] same.txt", "[2] same.txt" }), "row prefixes");
        var progress = new List<BackupProgress>();
        var result = await Engine.ExecuteAsync(plan, new InlineProgress<BackupProgress>(progress.Add));
        Check(result.Success && result.CopiedFiles == 2, "both rows restored");
        Check(Read(pairs[0].DestinationPath) == "FIRST" && Read(pairs[1].DestinationPath) == "SNAP", "paired same names");
        Check(File.ReadAllText(Path.Combine(pairs[1].DestinationPath, "keep.txt")) == "KEEP", "destination-only retained");
        Check(File.GetLastWriteTimeUtc(Path.Combine(pairs[1].DestinationPath, "same.txt")) == Stamp, "timestamp retained");
        Check(Directory.Exists(Path.Combine(pairs[0].DestinationPath, "空フォルダー")), "empty folder restored");
        Check(progress.Count > 0 && progress.All(p => p.TotalFiles == 2 && p.TotalBytes == 9) &&
            progress[^1].CompletedFiles == 2 && progress[^1].BytesCopied == 9, "aggregate progress");
        Check((await Engine.CreatePlanAsync(pairs)).Files.Count == 0, "repeat skips all files");
    }

    public static async Task StaleLaterRow()
    {
        using var f = new Fixture();
        var pairs = Pairs(f);
        foreach (var pair in pairs) { Write(pair.SourcePath, "same.txt", "SNAP"); Write(pair.DestinationPath, "same.txt", "OLD!"); }
        foreach (var changeSource in new[] { false, true })
        {
            var plan = await Engine.CreatePlanAsync(pairs);
            Write(changeSource ? pairs[1].SourcePath : pairs[1].DestinationPath, "same.txt", "EDIT");
            var result = await Engine.ExecuteAsync(plan);
            Check(!result.Success && result.CopiedFiles == 0 && result.Errors.Any(issue => issue.RelativePath.StartsWith("[2]")), "later row blocks whole batch");
            Check(Read(pairs[0].DestinationPath) == "OLD!", "earlier row untouched");
            Write(pairs[1].SourcePath, "same.txt", "SNAP"); Write(pairs[1].DestinationPath, "same.txt", "OLD!");
        }
        Write(pairs[1].DestinationPath, "same.txt", "SNAP");
        var unchangedPlan = await Engine.CreatePlanAsync(pairs);
        Write(pairs[1].DestinationPath, "same.txt", "EDIT");
        Check((await Engine.ExecuteAsync(unchangedPlan)).CopiedFiles == 0 && Read(pairs[0].DestinationPath) == "OLD!", "unchanged later file revalidated");
    }

    public static async Task Overlaps()
    {
        using var f = new Fixture();
        var pairs = Pairs(f);
        await Reject<ArgumentException>(() => Engine.CreatePlanAsync([pairs[0], pairs[1] with { DestinationPath = pairs[0].DestinationPath.ToUpperInvariant() }]));
        await Reject<ArgumentException>(() => Engine.CreatePlanAsync([pairs[0], pairs[1] with { DestinationPath = Path.Combine(pairs[0].DestinationPath, "nested") }]));
        await Reject<ArgumentException>(() => Engine.CreatePlanAsync([pairs[0] with { DestinationPath = pairs[1].SourcePath }, pairs[1]]));
        await Reject<ArgumentException>(() => Engine.CreatePlanAsync([pairs[0], pairs[1] with { SourcePath = pairs[0].SourcePath }]));
        await Reject<ArgumentException>(() => Engine.CreatePlanAsync([pairs[0] with { DestinationPath = f.Root }]));
        Check(!Directory.Exists(pairs[0].DestinationPath), "invalid mappings never write");
    }

    public static async Task InvalidRows()
    {
        using var f = new Fixture();
        var pairs = Pairs(f);
        Write(pairs[0].SourcePath, "same.txt", "FIRST");
        await Reject<ArgumentException>(() => Engine.CreatePlanAsync([]));
        await Reject<DirectoryNotFoundException>(() => Engine.CreatePlanAsync([pairs[0], pairs[1] with { SourcePath = Path.Combine(f.Root, "missing") }]));
        Write(pairs[1].SourcePath, "blocked.txt", "SOURCE");
        Directory.CreateDirectory(Path.Combine(pairs[1].DestinationPath, "blocked.txt"));
        var plan = await Engine.CreatePlanAsync(pairs);
        Check(plan.SkippedPaths.Count > 0, "unreviewable collision visible");
        var result = await Engine.ExecuteAsync(plan);
        Check(!result.Success && result.CopiedFiles == 0 && !Directory.Exists(pairs[0].DestinationPath), "one skipped row blocks entire batch");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Reject<OperationCanceledException>(() => Engine.CreatePlanAsync(pairs, canceled.Token));
        Check((await Engine.ExecuteAsync(plan, cancellationToken: canceled.Token)).IsCanceled, "pre-canceled execution");
    }

    public static async Task Cancellation()
    {
        using var f = new Fixture();
        var pairs = Pairs(f);
        Write(pairs[0].SourcePath, "same.txt", "FIRST");
        Write(pairs[1].SourcePath, "same.txt", new string('N', 3 * 1024 * 1024));
        Write(pairs[1].DestinationPath, "same.txt", "OLD");
        var plan = await Engine.CreatePlanAsync(pairs);
        using var stop = new CancellationTokenSource();
        var result = await Engine.ExecuteAsync(plan, new InlineProgress<BackupProgress>(p =>
        {
            if (p.RelativePath.StartsWith("[2]", StringComparison.Ordinal) && p.BytesCopied > 5) stop.Cancel();
        }), stop.Token);
        Check(result.IsCanceled && result.CopiedFiles == 1, "completed first row counted");
        Check(Read(pairs[0].DestinationPath) == "FIRST" && Read(pairs[1].DestinationPath) == "OLD", "completed row retained, active original preserved");
        Check(!Directory.GetFiles(f.Root, ".manualbackup-*.tmp", SearchOption.AllDirectories).Any(), "staging cleaned");
    }

    public static async Task Locks()
    {
        using var f = new Fixture();
        var pairs = Pairs(f);
        Write(pairs[0].SourcePath, "same.txt", new string('A', 2 * 1024 * 1024));
        Write(pairs[1].SourcePath, "same.txt", "SECOND");
        var plan = await Engine.CreatePlanAsync(pairs);
        var standalone = await f.Engine.CreatePlanAsync(new BackupRequest(pairs[1].SourcePath, pairs[1].DestinationPath, FileComparisonMode.Content));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var task = Engine.ExecuteAsync(plan, new InlineProgress<BackupProgress>(p =>
        {
            if (p.RelativePath.StartsWith("[1]", StringComparison.Ordinal) && p.CompletedFiles == 0)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
            }
        }));
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(10)), "first row entered");
            await Reject<IOException>(() => f.Engine.ExecuteAsync(standalone));
        }
        finally { release.Set(); }
        Check((await task).Success, "batch succeeds after lock test");
        // Conversely a later destination already in use must block the batch
        // before it modifies an earlier row.
        Write(pairs[0].DestinationPath, "same.txt", "OLD");
        Write(pairs[1].DestinationPath, "same.txt", "OLD");
        var secondPlan = await Engine.CreatePlanAsync(pairs);
        var heldPlan = await f.Engine.CreatePlanAsync(new BackupRequest(pairs[1].SourcePath, pairs[1].DestinationPath, FileComparisonMode.Content));
        entered.Reset(); release.Reset();
        var heldTask = f.Engine.ExecuteAsync(heldPlan, new InlineProgress<BackupProgress>(_ =>
        {
            entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
        }));
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(10)), "later destination locked");
            await Reject<IOException>(() => Engine.ExecuteAsync(secondPlan));
            Check(Read(pairs[0].DestinationPath) == "OLD", "first row unchanged after lock refusal");
        }
        finally { release.Set(); }
        Check((await heldTask).Success, "standalone completes");
    }

    public static async Task PartialFailure()
    {
        using var f = new Fixture();
        var pairs = Pairs(f);
        foreach (var pair in pairs) { Write(pair.SourcePath, "same.txt", "NEW"); Write(pair.DestinationPath, "same.txt", "OLD"); }
        var plan = await Engine.CreatePlanAsync(pairs);
        var result = await Engine.ExecuteAsync(plan, new InlineProgress<BackupProgress>(_ => throw new IOException("observer failed")));
        Check(!result.Success && result.CopiedFiles == 0 && Read(pairs[0].DestinationPath) == "OLD" && Read(pairs[1].DestinationPath) == "OLD", "observer failure preserves originals and skips later rows");
        Check(!Directory.GetFiles(f.Root, ".manualbackup-*.tmp", SearchOption.AllDirectories).Any(), "failed staging cleaned");
        var lockedPlan = await Engine.CreatePlanAsync(pairs);
        using (var reader = new FileStream(Path.Combine(pairs[0].DestinationPath, "same.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var lockedResult = await Engine.ExecuteAsync(lockedPlan);
            Check(!lockedResult.Success && lockedResult.CopiedFiles == 0 && Read(pairs[1].DestinationPath) == "OLD", "copy failure stops later rows");
        }
        Check(Read(pairs[0].DestinationPath) == "OLD", "locked destination unchanged");
    }

    private static RestoreFolderPair[] Pairs(Fixture f)
    {
        var second = Path.Combine(f.Root, "source-second"); Directory.CreateDirectory(second);
        return [new(f.Source, Path.Combine(f.Root, "restore-first")), new(second, Path.Combine(f.Root, "restore-second"))];
    }
    private static void Write(string root, string name, string text)
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, name);
        File.WriteAllText(path, text); File.SetLastWriteTimeUtc(path, Stamp);
    }
    private static string Read(string root) => File.ReadAllText(Path.Combine(root, "same.txt"));
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
