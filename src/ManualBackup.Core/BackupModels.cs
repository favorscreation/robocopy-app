namespace ManualBackup.Core;

public enum FileComparisonMode { SizeAndTimestamp, Content }

public sealed record BackupRequest(string SourcePath, string DestinationPath,
    FileComparisonMode ComparisonMode = FileComparisonMode.SizeAndTimestamp);

public enum BackupChangeKind { New, Updated }

public sealed record BackupFile(
    string RelativePath,
    string SourcePath,
    string DestinationPath,
    long Length,
    DateTime LastWriteTimeUtc,
    BackupChangeKind Kind);

public sealed record BackupIssue(string RelativePath, string Message);

/// <summary>A read-only preview. The engine checks planned source files again when copying.</summary>
public sealed class BackupPlan
{
    public string SourcePath { get; }
    public string DestinationPath { get; }
    public FileComparisonMode ComparisonMode { get; }
    public IReadOnlyList<BackupFile> Files { get; }
    public IReadOnlyList<string> Directories { get; }
    public int UnchangedFiles { get; }
    public IReadOnlyList<BackupIssue> SkippedPaths { get; }
    public long TotalBytes { get; }
    internal IReadOnlyDictionary<string, ContentFileState> ContentStates { get; }
    internal bool NeedsCanonicalTargetCheck { get; }

    internal BackupPlan(string sourcePath, string destinationPath, List<BackupFile> files,
        List<string> directories, int unchangedFiles, List<BackupIssue> skippedPaths,
        FileComparisonMode comparisonMode, Dictionary<string, ContentFileState> contentStates,
        bool needsCanonicalTargetCheck = false)
    {
        SourcePath = sourcePath;
        DestinationPath = destinationPath;
        ComparisonMode = comparisonMode;
        ContentStates = new System.Collections.ObjectModel.ReadOnlyDictionary<string, ContentFileState>(contentStates);
        NeedsCanonicalTargetCheck = needsCanonicalTargetCheck;
        Files = files.AsReadOnly();
        Directories = directories.AsReadOnly();
        UnchangedFiles = unchangedFiles;
        SkippedPaths = skippedPaths.AsReadOnly();
        TotalBytes = files.Sum(file => file.Length);
    }
}

internal sealed record FileSnapshot(bool Exists, long Length, DateTime LastWriteTimeUtc, string? Sha256);
internal sealed record ContentFileState(FileSnapshot Source, FileSnapshot Destination);

public sealed record BackupProgress(
    string RelativePath,
    int CompletedFiles,
    int TotalFiles,
    long BytesCopied,
    long TotalBytes);

public sealed record BackupResult(
    int CopiedFiles,
    long BytesCopied,
    bool IsCanceled,
    IReadOnlyList<BackupIssue> Errors,
    TimeSpan Duration)
{
    /// <summary>True only when every planned copy completed without errors or cancellation.</summary>
    public bool Success => !IsCanceled && Errors.Count == 0;
}
