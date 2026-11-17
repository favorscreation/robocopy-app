using ManualBackup.Core;

internal static class RestoreScenarios
{
    private static readonly DateTime Stamp = new(2026, 1, 2, 3, 4, 6, DateTimeKind.Utc);

    public static async Task RoundTrip()
    {
        using var f = new Fixture();
        Write(f.Source, "nested/old.txt", "SNAP", Stamp);
        Write(f.Source, "missing.txt", "MISSING", Stamp);
        Write(f.Source, "identical.txt", "SAME", Stamp);
        Directory.CreateDirectory(Path.Combine(f.Source, "empty-dir"));
        await f.Backup();
        Write(f.Source, "nested/old.txt", "EDIT", Stamp.AddDays(10));
        File.Delete(Path.Combine(f.Source, "missing.txt"));
        Write(f.Source, "destination-only.txt", "KEEP", Stamp);

        var plan = await f.Engine.CreatePlanAsync(new BackupRequest(f.Destination, f.Source, FileComparisonMode.Content));
        Equal(FileComparisonMode.Content, plan.ComparisonMode, "content mode bound to preview");
        Equal(1, plan.Files.Count(x => x.Kind == BackupChangeKind.New), "one missing file");
        Equal(1, plan.Files.Count(x => x.Kind == BackupChangeKind.Updated), "one overwritten file");
        Equal(1, plan.UnchangedFiles, "identical file skipped");
        Equal(0, plan.SkippedPaths.Count, "no omitted restore entries");
        var result = await f.Engine.ExecuteAsync(plan);
        Success(result);
        Equal(2, result.CopiedFiles, "only changed files restored");
        Equal("SNAP", Read(f.Source, "nested/old.txt"), "older snapshot restored over newer working copy");
        Equal(Stamp, File.GetLastWriteTimeUtc(Path.Combine(f.Source, "nested/old.txt")), "snapshot timestamp restored");
        Equal("MISSING", Read(f.Source, "missing.txt"), "missing file restored");
        Equal("KEEP", Read(f.Source, "destination-only.txt"), "destination-only file retained");
        Equal("SNAP", Read(f.Destination, "nested/old.txt"), "snapshot never modified");
        True(Directory.Exists(Path.Combine(f.Source, "empty-dir")), "empty directory retained");

        var repeat = await f.Engine.CreatePlanAsync(new BackupRequest(f.Destination, f.Source, FileComparisonMode.Content));
        Equal(0, repeat.Files.Count, "repeat has no changes");
        Equal(3, repeat.UnchangedFiles, "all snapshot files unchanged");
        var repeatResult = await f.Engine.ExecuteAsync(repeat);
        Success(repeatResult);
        Equal(0, repeatResult.CopiedFiles, "repeat makes no copies");
    }

    public static async Task SameMetadataDifference()
    {
        using var f = new Fixture();
        Write(f.Source, "a.txt", "SNAP", Stamp);
        Write(f.Destination, "a.txt", "EDIT", Stamp);
        var metadataPlan = await f.Plan();
        Equal(0, metadataPlan.Files.Count, "ordinary backup keeps metadata comparison semantics");
        var plan = await ContentPlan(f);
        Equal(1, plan.Files.Count, "same metadata but different content detected");
        Equal(BackupChangeKind.Updated, plan.Files[0].Kind, "content change classified as update");
        var result = await f.Engine.ExecuteAsync(plan);
        Success(result);
        Equal("SNAP", Read(f.Destination, "a.txt"), "snapshot bytes restored");
    }

    public static async Task DestinationChangedAfterPreview()
    {
        using var f = new Fixture();
        Write(f.Source, "a.txt", "SNAP", Stamp);
        Write(f.Source, "b.txt", "BACK", Stamp);
        Write(f.Destination, "a.txt", "OLD1", Stamp);
        Write(f.Destination, "b.txt", "OLD2", Stamp);
        var plan = await ContentPlan(f);
        Equal(2, plan.Files.Count, "two planned replacements");
        Write(f.Destination, "b.txt", "EDIT", Stamp);
        var result = await f.Engine.ExecuteAsync(plan);
        Rejected(result);
        Equal("OLD1", Read(f.Destination, "a.txt"), "preflight prevents earlier planned file from being written");
        Equal("EDIT", Read(f.Destination, "b.txt"), "post-preview working edit preserved");
        NoStaging(f.Destination);
    }

    public static async Task TimestampOnlyDifference()
    {
        using var f = new Fixture();
        Write(f.Source, "a.txt", "SAME", Stamp);
        Write(f.Destination, "a.txt", "SAME", Stamp.AddDays(5));
        var plan = await ContentPlan(f);
        Equal(1, plan.Files.Count, "timestamp-only difference still restores snapshot metadata");
        Equal(BackupChangeKind.Updated, plan.Files[0].Kind, "timestamp-only difference classified as update");
        var result = await f.Engine.ExecuteAsync(plan);
        Success(result);
        Equal(Stamp, File.GetLastWriteTimeUtc(Path.Combine(f.Destination, "a.txt")), "older snapshot timestamp restored");
        Equal(0, (await ContentPlan(f)).Files.Count, "timestamp restoration leaves no repeat difference");
    }

    public static async Task NewDestinationAppeared()
    {
        using var f = new Fixture();
        Write(f.Source, "a.txt", "SNAP", Stamp);
        Write(f.Source, "b.txt", "BACK", Stamp);
        var plan = await ContentPlan(f);
        True(!Directory.Exists(f.Destination), "preview has not created destination");
        Write(f.Destination, "b.txt", "USER", Stamp);
        var result = await f.Engine.ExecuteAsync(plan);
        Rejected(result);
        True(!File.Exists(Path.Combine(f.Destination, "a.txt")), "preflight stops other new files");
        Equal("USER", Read(f.Destination, "b.txt"), "unreviewed newly appeared target retained");
        NoStaging(f.Destination);
    }

    public static async Task SourceChangedAfterPreview()
    {
        using var f = new Fixture();
        Write(f.Source, "a.txt", "SNAP", Stamp);
        Write(f.Destination, "a.txt", "OLD1", Stamp);
        var plan = await ContentPlan(f);
        Write(f.Source, "a.txt", "EDIT", Stamp);
        var result = await f.Engine.ExecuteAsync(plan);
        Rejected(result);
        Equal("OLD1", Read(f.Destination, "a.txt"), "unreviewed source bytes not copied");
        Equal("EDIT", Read(f.Source, "a.txt"), "source never written");
        NoStaging(f.Destination);
    }

    public static async Task UnchangedFilesChangedAfterPreview()
    {
        foreach (var mutateSource in new[] { false, true })
        {
            using var f = new Fixture();
            Write(f.Source, "a.txt", "SAME", Stamp);
            Write(f.Destination, "a.txt", "SAME", Stamp);
            Write(f.Source, "b.txt", "BACK", Stamp);
            Write(f.Destination, "b.txt", "OLD2", Stamp);
            var plan = await ContentPlan(f);
            Equal(1, plan.UnchangedFiles, "one unchanged entry");
            Equal(1, plan.Files.Count, "one planned replacement");
            Write(mutateSource ? f.Source : f.Destination, "a.txt", "EDIT", Stamp);
            var result = await f.Engine.ExecuteAsync(plan);
            Rejected(result);
            Equal("OLD2", Read(f.Destination, "b.txt"), "stale unchanged entry prevents other writes");
            Equal(mutateSource ? "SAME" : "EDIT", Read(f.Destination, "a.txt"), "working copy preserved");
        }
    }

    public static async Task PreviewSafety()
    {
        using var f = new Fixture();
        Write(f.Source, "nested/a.txt", "SNAP", Stamp);
        Write(f.Source, "empty.bin", "", Stamp);
        var initialPaths = Directory.GetFileSystemEntries(f.Root).Order().ToArray();
        var plan = await ContentPlan(f);
        Equal(2, plan.Files.Count, "empty and nonempty files planned");
        True(!Directory.Exists(f.Destination), "restore preview never creates destination");
        True(initialPaths.SequenceEqual(Directory.GetFileSystemEntries(f.Root).Order()), "restore preview creates no output");
        foreach (var destination in new[] { f.Source, Path.Combine(f.Source, "nested"), f.Root })
        {
            try
            {
                _ = await f.Engine.CreatePlanAsync(new BackupRequest(f.Source, destination, FileComparisonMode.Content));
                throw new Exception("Overlapping restore paths were accepted.");
            }
            catch (ArgumentException) { }
        }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            _ = await f.Engine.CreatePlanAsync(new BackupRequest(f.Source, f.Destination, FileComparisonMode.Content), canceled.Token);
            throw new Exception("Canceled restore preview was not interrupted.");
        }
        catch (OperationCanceledException) { }
    }

    public static async Task Cancellation()
    {
        using var f = new Fixture();
        var data = new byte[8 * 1024 * 1024];
        new Random(20261113).NextBytes(data);
        File.WriteAllBytes(Path.Combine(f.Source, "large.bin"), data);
        Write(f.Destination, "large.bin", "ORIGINAL", Stamp);
        var plan = await ContentPlan(f);
        using var ct = new CancellationTokenSource();
        var progressObserved = false;
        var result = await f.Engine.ExecuteAsync(plan, new InlineProgress<BackupProgress>(p =>
        {
            if (p.BytesCopied > 0 && p.CompletedFiles == 0)
            {
                progressObserved = true;
                ct.Cancel();
            }
        }), ct.Token);
        True(progressObserved && result.IsCanceled && !result.Success, "restore canceled after actual data copy began");
        Equal("ORIGINAL", Read(f.Destination, "large.bin"), "existing working file survives cancellation");
        Equal(0, result.CopiedFiles, "canceled staging never committed");
        NoStaging(f.Destination);
    }

    public static async Task DestinationChangedDuringCopy()
    {
        using var f = new Fixture();
        var data = new byte[2 * 1024 * 1024];
        new Random(27).NextBytes(data);
        File.WriteAllBytes(Path.Combine(f.Source, "large.bin"), data);
        Write(f.Destination, "large.bin", "OLD1", Stamp);
        var plan = await ContentPlan(f);
        var attemptedEdit = false;
        var editSucceeded = false;
        var result = await f.Engine.ExecuteAsync(plan, new InlineProgress<BackupProgress>(p =>
        {
            if (attemptedEdit || p.BytesCopied == 0 || p.CompletedFiles != 0) return;
            attemptedEdit = true;
            try { Write(f.Destination, "large.bin", "EDIT", Stamp); editSucceeded = true; }
            catch (IOException) { }
        }));
        True(attemptedEdit, "attempted concurrent working-file edit during actual copy");
        if (editSucceeded)
        {
            Rejected(result);
            Equal("EDIT", Read(f.Destination, "large.bin"), "unreviewed edit is never overwritten");
        }
        else
        {
            // A locked destination is also a valid way to prevent concurrent edits.
            Success(result);
            True(data.SequenceEqual(File.ReadAllBytes(Path.Combine(f.Destination, "large.bin"))), "snapshot committed while destination locked");
        }
        NoStaging(f.Destination);
    }

    private static Task<BackupPlan> ContentPlan(Fixture f) => f.Engine.CreatePlanAsync(new BackupRequest(f.Source, f.Destination, FileComparisonMode.Content));
    private static string Read(string root, string relative) => File.ReadAllText(Path.Combine(root, relative));
    private static void Write(string root, string relative, string value, DateTime stamp)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
        File.SetLastWriteTimeUtc(path, stamp);
    }
    private static void NoStaging(string destination) => True(!Directory.GetFiles(destination, ".manualbackup-*.tmp", SearchOption.AllDirectories).Any(), "no staging files left");
    private static void Success(BackupResult result) => True(result.Success, string.Join("; ", result.Errors.Select(x => x.RelativePath + ": " + x.Message)));
    private static void Rejected(BackupResult result)
    {
        True(!result.Success && !result.IsCanceled && result.Errors.Count > 0, "changed preview reports an error");
        Equal(0, result.CopiedFiles, "stale restore makes no committed copies");
    }
    private static void True(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{message}: expected {expected}, actual {actual}");
    }
}
