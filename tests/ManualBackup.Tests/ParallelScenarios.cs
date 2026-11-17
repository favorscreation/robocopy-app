using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using ManualBackup.Core;

internal static class ParallelScenarios
{
    public static Task SerialLimit() => VerifyCopyLimit(1);
    public static Task TwoSourcesAtOnce() => VerifyCopyLimit(2);
    public static Task FourSourcesAtOnce() => VerifyCopyLimit(4);

    private static async Task VerifyCopyLimit(int limit)
    {
        using var f = new Fixture();
        var sources = CreateSources(f, 6, 2 * 1024 * 1024, includeEmptyFiles: true);
        var engine = new MultiBackupEngine();
        // Exercise the old two-argument request as the sequential compatibility path.
        var request = limit == 1 ? new MultiBackupRequest(sources, f.Destination) : new MultiBackupRequest(sources, f.Destination, limit);
        var plan = await engine.CreatePlanAsync(request);
        Equal(limit, plan.MaxParallelSources, "chosen limit is retained by the preview");
        var updates = new List<BackupProgress>();
        var observedPeak = 0;
        CopyGate? observation = null;
        var result = await WithCopyGate(engine, plan, async gate =>
        {
            observation = gate;
            await WaitForStaging(gate, limit);
            // Keep the first real copy blocked briefly. Every other admitted worker
            // holds its own real staging file while waiting to report its first chunk.
            var hold = Stopwatch.StartNew();
            while (hold.Elapsed < TimeSpan.FromMilliseconds(150))
            {
                gate.ObserveStaging();
                True(gate.PeakStaging <= limit, $"more than {limit} sources copied simultaneously");
                await Task.Delay(10);
            }
            observedPeak = gate.PeakStaging;
        }, updates);
        observedPeak = observation!.PeakStaging;
        Success(result);
        Equal(limit, observedPeak, "simultaneous real staging files reach the selected cap");
        Equal(plan.Files.Count, result.CopiedFiles, "all files, including empty files, copied");
        Equal(plan.TotalBytes, result.BytesCopied, "all source bytes copied");
        ValidateProgress(updates, plan, requireAllCopied: true);
        VerifyContents(sources, f.Destination);
        NoStaging(f.Destination);
        Console.WriteLine($"EVIDENCE parallel cap={limit}: simultaneous active staging files={observedPeak}; sources={sources.Count}; bytes={plan.TotalBytes}.");
    }

    public static async Task InvalidLimits()
    {
        using var f = new Fixture();
        var sources = CreateSources(f, 2, 1024);
        var engine = new MultiBackupEngine();
        foreach (var invalid in new[] { -1, 0, 9, int.MaxValue })
            await Throws<ArgumentException>(() => engine.CreatePlanAsync(new MultiBackupRequest(sources, f.Destination, invalid)));
        True(!Directory.Exists(f.Destination), "invalid concurrency limits create no destination files or folders");
        var maximum = await engine.CreatePlanAsync(new MultiBackupRequest(sources, f.Destination, 8));
        Equal(8, maximum.MaxParallelSources, "inclusive maximum is accepted");
        True(!Directory.Exists(f.Destination), "valid preview remains read only");
    }

    public static async Task ParallelLocksCoverAllTargets()
    {
        using var f = new Fixture();
        var sources = CreateSources(f, 4, 2 * 1024 * 1024);
        var engine = new MultiBackupEngine();
        var plan = await engine.CreatePlanAsync(new MultiBackupRequest(sources, f.Destination, 2));
        var alternativeSources = sources.Select((source, index) => source with { TargetFolderName = "alternative-" + index }).ToArray();
        var competingBatch = await engine.CreatePlanAsync(new MultiBackupRequest(alternativeSources, f.Destination, 2));
        var restoreSource = Path.Combine(f.Root, "restore-source");
        Directory.CreateDirectory(restoreSource);
        File.WriteAllText(Path.Combine(restoreSource, "restore-marker.txt"), "RESTORE");
        var restoreEngine = new BackupEngine();
        // The last child has not started when the first two workers reach the gate.
        var restorePlan = await restoreEngine.CreatePlanAsync(new BackupRequest(restoreSource,
            Path.Combine(f.Destination, sources[^1].TargetFolderName), FileComparisonMode.Content));
        var result = await WithCopyGate(engine, plan, async gate =>
        {
            await WaitForStaging(gate, 2);
            await Throws<IOException>(() => engine.ExecuteAsync(competingBatch));
            await Throws<IOException>(() => restoreEngine.ExecuteAsync(restorePlan));
            True(!Directory.Exists(Path.Combine(f.Destination, "alternative-0")), "competing batch cannot write another child of the locked root");
            True(!File.Exists(Path.Combine(f.Destination, sources[^1].TargetFolderName, "restore-marker.txt")), "restore cannot write a future worker's child while the batch is active");
        });
        Success(result);
        // This reviewed target marker is still absent. Successful restore proves the
        // coordinator released the child mutex after every parallel worker joined.
        Success(await restoreEngine.ExecuteAsync(restorePlan));
        Equal("RESTORE", File.ReadAllText(Path.Combine(f.Destination, sources[^1].TargetFolderName, "restore-marker.txt")), "restore can write after the parallel operation returns");
    }

    public static async Task ParallelCancellationJoinsWorkers()
    {
        using var f = new Fixture();
        var sources = CreateSources(f, 4, 4 * 1024 * 1024);
        CreateOriginalDestinations(sources, f.Destination);
        var engine = new MultiBackupEngine();
        var plan = await engine.CreatePlanAsync(new MultiBackupRequest(sources, f.Destination, 2));
        using var cancel = new CancellationTokenSource();
        var updates = new List<BackupProgress>();
        var cancellationWatch = new Stopwatch();
        var result = await WithCopyGate(engine, plan, async gate =>
        {
            await WaitForStaging(gate, 2);
            cancellationWatch.Start();
            cancel.Cancel();
        }, updates, cancel.Token);
        cancellationWatch.Stop();
        True(result.IsCanceled && !result.Success, "external cancellation returns canceled status");
        Equal(0, result.CopiedFiles, "mid-chunk cancellation commits none of the gated files");
        True(cancellationWatch.Elapsed < TimeSpan.FromSeconds(5), "all local-copy workers stop promptly after cancellation");
        VerifyOriginals(sources, f.Destination);
        NoStaging(f.Destination);
        int reportCount;
        lock (updates) reportCount = updates.Count;
        var snapshot = Snapshot(f.Destination);
        await Task.Delay(300);
        lock (updates) Equal(reportCount, updates.Count, "no worker reports progress after ExecuteAsync has returned");
        True(snapshot.SequenceEqual(Snapshot(f.Destination)), "no destination writes continue after ExecuteAsync returns");
        NoStaging(f.Destination);
        // A second operation on the same plan must acquire all locks and finish.
        Success(await engine.ExecuteAsync(plan));
        VerifyContents(sources, f.Destination);
    }

    public static async Task ParallelFileFailureContinues()
    {
        using var f = new Fixture();
        var sources = CreateSources(f, 4, 2 * 1024 * 1024);
        CreateOriginalDestinations(sources, f.Destination);
        var engine = new MultiBackupEngine();
        var plan = await engine.CreatePlanAsync(new MultiBackupRequest(sources, f.Destination, 2));
        var updates = new List<BackupProgress>();
        BackupResult result;
        using (File.Open(Path.Combine(f.Destination, sources[0].TargetFolderName, "blob.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
            result = await engine.ExecuteAsync(plan, new InlineProgress<BackupProgress>(p => { lock (updates) updates.Add(p); }));
        True(!result.Success && !result.IsCanceled, "one worker's file error fails the batch without canceling independent workers");
        Equal(3, result.CopiedFiles, "all other source workers complete");
        Equal(1, result.Errors.Count, "one locked-file error returned");
        Equal(Path.Combine(sources[0].TargetFolderName, "blob.bin"), result.Errors.Single().RelativePath, "parallel error identifies the correct source child");
        Equal("ORIGINAL-0", File.ReadAllText(Path.Combine(f.Destination, sources[0].TargetFolderName, "blob.bin")), "failed target's original data remains intact");
        VerifyContents(sources.Skip(1).ToArray(), f.Destination);
        NoStaging(f.Destination);
        ValidateProgress(updates, plan, requireAllCopied: false);
    }

    public static async Task ProgressFailureJoinsWorkers()
    {
        using var f = new Fixture();
        var sources = CreateSources(f, 4, 4 * 1024 * 1024);
        CreateOriginalDestinations(sources, f.Destination);
        var engine = new MultiBackupEngine();
        var plan = await engine.CreatePlanAsync(new MultiBackupRequest(sources, f.Destination, 2));
        var reportCount = 0;
        var result = await engine.ExecuteAsync(plan, new InlineProgress<BackupProgress>(p =>
        {
            Interlocked.Increment(ref reportCount);
            if (p.BytesCopied > 0) throw new InvalidOperationException("Injected progress observer failure");
        })).WaitAsync(TimeSpan.FromSeconds(10));
        True(!result.Success && result.Errors.Count > 0, "observer failure is returned as an operation error");
        True(!result.IsCanceled, "observer failure is distinct from external user cancellation");
        True(result.Errors.Any(e => e.Message.Contains("Injected progress observer failure", StringComparison.Ordinal)), "observer failure reason is retained");
        NoStaging(f.Destination);
        var reportsAtReturn = reportCount;
        var snapshot = Snapshot(f.Destination);
        await Task.Delay(300);
        Equal(reportsAtReturn, reportCount, "failed observer is not called by workers after return");
        True(snapshot.SequenceEqual(Snapshot(f.Destination)), "observer failure joins every writing worker before returning");
        Success(await engine.ExecuteAsync(plan));
        VerifyContents(sources, f.Destination);
    }

    private static async Task<BackupResult> WithCopyGate(MultiBackupEngine engine, MultiBackupPlan plan,
        Func<CopyGate, Task> inspect, List<BackupProgress>? updates = null, CancellationToken externalToken = default)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        using var gate = new CopyGate(plan.DestinationPath, updates);
        var work = engine.ExecuteAsync(plan, gate, cancel.Token);
        Exception? inspectionFailure = null;
        try
        {
            True(gate.Entered.Wait(TimeSpan.FromSeconds(10)), "a real worker reached the first copied chunk");
            await inspect(gate);
        }
        catch (Exception exception) { inspectionFailure = exception; cancel.Cancel(); }
        finally { gate.Release.Set(); }
        // Always join the operation before disposing the test-owned filesystem,
        // including when an assertion in the gated observer fails.
        var result = await work.WaitAsync(TimeSpan.FromSeconds(20));
        if (inspectionFailure is not null) ExceptionDispatchInfo.Capture(inspectionFailure).Throw();
        return result;
    }

    private static async Task WaitForStaging(CopyGate gate, int expected)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            var count = gate.ObserveStaging();
            True(count <= expected, $"staging files exceeded selected concurrency cap {expected}: {count}");
            if (count == expected) return;
            await Task.Delay(10);
        }
        throw new Exception($"Expected {expected} simultaneous real staging files; peak observed {gate.PeakStaging}.");
    }

    private static IReadOnlyList<BackupSource> CreateSources(Fixture f, int count, int fileBytes, bool includeEmptyFiles = false)
    {
        var sources = new List<BackupSource>();
        for (var i = 0; i < count; i++)
        {
            var source = Path.Combine(f.Root, "parallel-source-" + i);
            Directory.CreateDirectory(source);
            using (var stream = File.Open(Path.Combine(source, "blob.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(fileBytes);
                stream.Write(Encoding.UTF8.GetBytes("UNIQUE-SOURCE-" + i));
                stream.Position = fileBytes - 64;
                stream.Write(Encoding.UTF8.GetBytes("TAIL-" + i));
            }
            if (includeEmptyFiles) File.WriteAllBytes(Path.Combine(source, "empty.txt"), []);
            sources.Add(new BackupSource(source, "folder-" + i));
        }
        return sources.AsReadOnly();
    }

    private static void CreateOriginalDestinations(IReadOnlyList<BackupSource> sources, string destination)
    {
        for (var i = 0; i < sources.Count; i++)
        {
            var directory = Path.Combine(destination, sources[i].TargetFolderName);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "blob.bin"), "ORIGINAL-" + i);
        }
    }

    private static void VerifyOriginals(IReadOnlyList<BackupSource> sources, string destination)
    {
        for (var i = 0; i < sources.Count; i++)
            Equal("ORIGINAL-" + i, File.ReadAllText(Path.Combine(destination, sources[i].TargetFolderName, "blob.bin")), "canceled target original retained");
    }

    private static void VerifyContents(IReadOnlyList<BackupSource> sources, string destination)
    {
        foreach (var source in sources)
        {
            foreach (var original in Directory.GetFiles(source.SourcePath, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, source.TargetFolderName, Path.GetRelativePath(source.SourcePath, original));
                using var input = File.OpenRead(original);
                using var output = File.OpenRead(target);
                True(SHA256.HashData(input).SequenceEqual(SHA256.HashData(output)), "copied file content matches its own source: " + target);
                Equal(File.GetLastWriteTimeUtc(original), File.GetLastWriteTimeUtc(target), "copied timestamp retained");
            }
        }
    }

    private static void ValidateProgress(List<BackupProgress> updates, MultiBackupPlan plan, bool requireAllCopied)
    {
        True(updates.Count > 0, "parallel progress emitted");
        var prefixes = plan.Entries.Select(e => e.Source.TargetFolderName + Path.DirectorySeparatorChar).ToArray();
        for (var i = 0; i < updates.Count; i++)
        {
            var value = updates[i];
            Equal(plan.Files.Count, value.TotalFiles, "progress total files covers the entire batch");
            Equal(plan.TotalBytes, value.TotalBytes, "progress total bytes covers the entire batch");
            True(value.CompletedFiles >= 0 && value.CompletedFiles <= value.TotalFiles, "completed count is within total");
            True(value.BytesCopied >= 0 && value.BytesCopied <= value.TotalBytes, "byte progress is within total");
            True(prefixes.Any(prefix => value.RelativePath.StartsWith(prefix, StringComparison.Ordinal)), "progress path includes its source child");
            if (i > 0)
            {
                True(value.CompletedFiles >= updates[i - 1].CompletedFiles, "completed-file progress never decreases across workers");
                True(value.BytesCopied >= updates[i - 1].BytesCopied, "byte progress never decreases across workers, including failed copies");
            }
        }
        Equal(plan.Files.Count, updates[^1].CompletedFiles, "last progress completes all attempted files");
        if (requireAllCopied) Equal(plan.TotalBytes, updates[^1].BytesCopied, "last clean progress reaches total bytes");
    }

    private static string[] Snapshot(string destination) => Directory.GetFiles(destination, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(destination, path) + "|" + new FileInfo(path).Length + "|" + File.GetLastWriteTimeUtc(path).Ticks).ToArray();

    private static void NoStaging(string destination) => True(!Directory.GetFiles(destination, ".manualbackup-*.tmp", SearchOption.AllDirectories).Any(), "no worker staging files remain");
    private static void Success(BackupResult result) => True(result.Success, string.Join("; ", result.Errors.Select(e => e.RelativePath + ": " + e.Message)));
    private static void True(bool value, string message) { if (!value) throw new Exception(message); }
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

    private sealed class CopyGate(string destination, List<BackupProgress>? updates) : IProgress<BackupProgress>, IDisposable
    {
        private int _firstChunk, _peakStaging;
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public int PeakStaging => Volatile.Read(ref _peakStaging);
        public int ObserveStaging()
        {
            var count = Directory.Exists(destination) ? Directory.GetFiles(destination, ".manualbackup-*.tmp", SearchOption.AllDirectories).Length : 0;
            var previous = Volatile.Read(ref _peakStaging);
            while (count > previous)
            {
                var observed = Interlocked.CompareExchange(ref _peakStaging, count, previous);
                if (observed == previous) break;
                previous = observed;
            }
            return count;
        }
        public void Report(BackupProgress value)
        {
            if (updates is not null) { lock (updates) updates.Add(value); }
            ObserveStaging();
            if (value.BytesCopied > 0 && Interlocked.CompareExchange(ref _firstChunk, 1, 0) == 0)
            {
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("The test did not release its real copy gate.");
            }
        }
        public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); }
    }
}
