using System.Diagnostics;
using ManualBackup.Core;

internal static class RestoreRetryScenarios
{
    private static readonly DateTime Stamp = new(2026, 1, 2, 3, 4, 6, DateTimeKind.Utc);

    public static async Task PermanentLock()
    {
        WindowsOnly();
        using var f = CreateFixture();
        var plan = await Plan(f);
        using var held = LockDestination(f);
        var watch = Stopwatch.StartNew();
        var result = await f.Engine.ExecuteAsync(plan);
        Check(!result.Success && !result.IsCanceled && result.CopiedFiles == 0 && result.Errors.Count == 1,
            "permanent delete-sharing lock reports failure without committing");
        Check(watch.ElapsedMilliseconds >= 500 && watch.Elapsed.TotalSeconds < 10, "retry wait is bounded");
        Check(File.ReadAllText(Path.Combine(f.Destination, "a.txt")) == "EDIT", "original survives failed retries");
        NoSidecars(f);
    }

    public static async Task TransientLock()
    {
        WindowsOnly();
        using var f = CreateFixture();
        var plan = await Plan(f);
        using var held = LockDestination(f);
        using var copied = new ManualResetEventSlim();
        var released = false;
        var release = new Thread(() =>
        {
            copied.Wait();
            Thread.Sleep(180);
            held.Dispose();
            released = true;
        }) { IsBackground = true };
        release.Start();
        BackupResult result;
        var watch = Stopwatch.StartNew();
        try
        {
            result = await f.Engine.ExecuteAsync(plan, new InlineProgress<BackupProgress>(p =>
            {
                if (p.BytesCopied > 0) copied.Set();
            }));
        }
        finally { copied.Set(); Check(release.Join(5000), "lock releaser completes"); }
        Check(released && result.Success && result.CopiedFiles == 1, "temporary lock is retried successfully");
        Check(watch.ElapsedMilliseconds >= 100, "real delete-sharing lock delayed the replacement");
        Check(File.ReadAllText(Path.Combine(f.Destination, "a.txt")) == "SNAP", "snapshot committed after lock release");
        Check(File.GetLastWriteTimeUtc(Path.Combine(f.Destination, "a.txt")) == Stamp, "snapshot timestamp committed");
        NoSidecars(f);
    }

    public static async Task CancellationDuringRetry()
    {
        WindowsOnly();
        using var f = CreateFixture();
        var plan = await Plan(f);
        using var held = LockDestination(f);
        using var stop = new CancellationTokenSource();
        using var copied = new ManualResetEventSlim();
        var cancel = new Thread(() => { copied.Wait(); Thread.Sleep(150); stop.Cancel(); }) { IsBackground = true };
        cancel.Start();
        BackupResult result;
        try
        {
            result = await f.Engine.ExecuteAsync(plan, new InlineProgress<BackupProgress>(p =>
            {
                if (p.BytesCopied > 0) copied.Set();
            }), stop.Token);
        }
        finally { copied.Set(); Check(cancel.Join(5000), "retry cancellation completes"); }
        Check(result.IsCanceled && result.CopiedFiles == 0, "cancellation interrupts replacement retry");
        Check(File.ReadAllText(Path.Combine(f.Destination, "a.txt")) == "EDIT", "canceled retry preserves original");
        NoSidecars(f);
    }

    public static async Task ChangedFilesAreRevalidated()
    {
        WindowsOnly();
        foreach (var changed in new[] { "source", "destination", "staging" })
        {
            using var f = CreateFixture();
            var plan = await Plan(f);
            var file = plan.Files.Single();
            var staging = Path.Combine(f.Destination, ".manualbackup-retry-test.tmp");
            File.Copy(file.SourcePath, staging);
            File.SetLastWriteTimeUtc(staging, Stamp);
            var attempts = 0;
            IOException? failure = null;
            try
            {
                BackupEngine.ReplaceReviewedFile(staging, file, plan.ContentStates[file.RelativePath], CancellationToken.None,
                    (temporary, destination, original) =>
                    {
                        attempts++;
                        if (attempts == 1)
                        {
                            // A native sharing failure leaves names unchanged. Make
                            // a concurrent same-metadata edit before the next attempt.
                            var path = changed == "source" ? file.SourcePath : changed == "destination" ? destination : temporary;
                            File.WriteAllText(path, "USER");
                            File.SetLastWriteTimeUtc(path, Stamp);
                            throw new IOException("sharing violation", unchecked((int)0x80070020));
                        }
                        File.Replace(temporary, destination, original, ignoreMetadataErrors: true);
                    });
            }
            catch (IOException exception) { failure = exception; }
            Check(failure is not null && attempts == 1, changed + " edit blocks every subsequent replace attempt");
            Check(File.ReadAllText(file.DestinationPath) == (changed == "destination" ? "USER" : "EDIT"),
                changed + " edit never overwrites the unreviewed target");
            Check(File.ReadAllText(file.SourcePath) == (changed == "source" ? "USER" : "SNAP"), "source data remains correct");
            NoSidecars(f);
        }
    }

    private static Fixture CreateFixture()
    {
        var f = new Fixture();
        f.WriteSource("a.txt", "SNAP");
        File.SetLastWriteTimeUtc(Path.Combine(f.Source, "a.txt"), Stamp);
        Directory.CreateDirectory(f.Destination);
        File.WriteAllText(Path.Combine(f.Destination, "a.txt"), "EDIT");
        File.SetLastWriteTimeUtc(Path.Combine(f.Destination, "a.txt"), Stamp);
        return f;
    }
    private static FileStream LockDestination(Fixture f) => new(Path.Combine(f.Destination, "a.txt"),
        FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    private static Task<BackupPlan> Plan(Fixture f) => f.Engine.CreatePlanAsync(new BackupRequest(f.Source, f.Destination, FileComparisonMode.Content));
    private static void NoSidecars(Fixture f) => Check(!Directory.GetFiles(f.Destination, ".manualbackup-*", SearchOption.AllDirectories).Any(), "no safe-failure staging or original sidecars remain");
    private static void WindowsOnly() { if (!OperatingSystem.IsWindows()) throw new TestSkippedException("Windows file-sharing semantics required"); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
