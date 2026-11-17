using ManualBackup.Core;

internal static class RestoreReplacementFailureScenarios
{
    public static Task UnableToMoveReplacementRetainsData() => Run(1176, moveOriginal: false, newTarget: false);
    public static Task RenamedOriginalIsRecovered() => Run(1177, moveOriginal: true, newTarget: false);
    public static Task RecoveryNeverOverwritesNewTarget() => Run(1177, moveOriginal: true, newTarget: true);

    private static async Task Run(int code, bool moveOriginal, bool newTarget)
    {
        using var f = new Fixture();
        var stamp = new DateTime(2026, 1, 2, 3, 4, 6, DateTimeKind.Utc);
        f.WriteSource("a.txt", "SNAP");
        File.SetLastWriteTimeUtc(Path.Combine(f.Source, "a.txt"), stamp);
        Directory.CreateDirectory(f.Destination);
        var destination = Path.Combine(f.Destination, "a.txt");
        File.WriteAllText(destination, "ORIGINAL");
        File.SetLastWriteTimeUtc(destination, stamp);
        var plan = await f.Engine.CreatePlanAsync(new BackupRequest(f.Source, f.Destination, FileComparisonMode.Content));
        var file = plan.Files.Single();
        var staging = Path.Combine(f.Destination, ".manualbackup-test.tmp");
        File.Copy(file.SourcePath, staging);
        File.SetLastWriteTimeUtc(staging, file.LastWriteTimeUtc);
        var attempts = 0;
        string? originalBackup = null;
        IOException? failure = null;
        try
        {
            BackupEngine.ReplaceReviewedFile(staging, file, plan.ContentStates[file.RelativePath], CancellationToken.None,
                (temporary, target, backup) =>
                {
                    attempts++;
                    originalBackup = backup;
                    // Reproduce documented Windows failure states using real files.
                    if (moveOriginal) File.Move(target, backup);
                    if (newTarget) File.WriteAllText(target, "USER");
                    throw new IOException("Simulated native replacement state", unchecked((int)0x80070000) | code);
                });
        }
        catch (IOException exception) { failure = exception; }
        if (failure is null) throw new Exception("Uncertain replacement was not reported.");
        Check(attempts == 1, "uncertain replacement is reported without retry");
        Check(File.ReadAllText(file.SourcePath) == "SNAP", "snapshot source is unchanged");
        Check(File.ReadAllText(staging) == "SNAP", "staged copy remains available");
        Check(failure.Message.Contains(staging, StringComparison.Ordinal), "error identifies retained staging path");
        Check(File.ReadAllText(destination) == (newTarget ? "USER" : "ORIGINAL"), "original or newly appeared target is retained");
        if (newTarget)
        {
            if (originalBackup is null) throw new Exception("Original backup path was not supplied.");
            Check(File.ReadAllText(originalBackup) == "ORIGINAL", "displaced original is retained separately");
            Check(failure.Message.Contains(originalBackup, StringComparison.Ordinal), "error identifies retained original path");
        }
        else
        {
            Check(File.GetLastWriteTimeUtc(destination) == stamp, "original timestamp retained or recovered");
            Check(!File.Exists(originalBackup), "recovered original is back at its reviewed destination");
        }
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
