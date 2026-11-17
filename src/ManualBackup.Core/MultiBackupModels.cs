namespace ManualBackup.Core;

public sealed record BackupSource(string SourcePath, string TargetFolderName);
public sealed record MultiBackupRequest(IReadOnlyList<BackupSource> Sources, string DestinationPath,
    int MaxParallelSources = 1);
public sealed record BackupSourcePlan(BackupSource Source, BackupPlan Plan);

/// <summary>A read-only batch preview. Files have destination-relative display paths.</summary>
public sealed class MultiBackupPlan
{
    public string DestinationPath { get; }
    public int MaxParallelSources { get; }
    public IReadOnlyList<BackupSourcePlan> Entries { get; }
    public IReadOnlyList<BackupFile> Files { get; }
    public int UnchangedFiles { get; }
    public long TotalBytes { get; }
    public IReadOnlyList<BackupIssue> SkippedPaths { get; }
    internal string CanonicalDestinationPath { get; }
    internal IReadOnlyList<string> CanonicalSourcePaths { get; }
    internal IReadOnlyList<string> CanonicalTargetPaths { get; }

    internal MultiBackupPlan(string destinationPath, string canonicalDestinationPath,
        List<BackupSourcePlan> entries, List<string> canonicalSources, List<string> canonicalTargets,
        int maxParallelSources = 1)
    {
        DestinationPath = destinationPath;
        MaxParallelSources = maxParallelSources;
        CanonicalDestinationPath = canonicalDestinationPath;
        Entries = entries.AsReadOnly();
        CanonicalSourcePaths = canonicalSources.AsReadOnly();
        CanonicalTargetPaths = canonicalTargets.AsReadOnly();
        Files = entries.SelectMany(entry => entry.Plan.Files.Select(file =>
            file with { RelativePath = Prefix(entry.Source.TargetFolderName, file.RelativePath) })).ToList().AsReadOnly();
        UnchangedFiles = entries.Sum(entry => entry.Plan.UnchangedFiles);
        TotalBytes = entries.Sum(entry => entry.Plan.TotalBytes);
        SkippedPaths = entries.SelectMany(entry => entry.Plan.SkippedPaths.Select(issue =>
            issue with { RelativePath = Prefix(entry.Source.TargetFolderName, issue.RelativePath) })).ToList().AsReadOnly();
    }

    internal static string Prefix(string targetFolder, string relative) =>
        string.IsNullOrEmpty(targetFolder) ? relative : string.IsNullOrEmpty(relative) ? targetFolder : Path.Combine(targetFolder, relative);
}
