using System.Diagnostics;

namespace ManualBackup.Core;

public sealed record RestoreFolderPair(string SourcePath, string DestinationPath);

public sealed class MultiRestorePlan
{
    public IReadOnlyList<BackupPlan> Entries { get; }
    public IReadOnlyList<BackupFile> Files { get; }
    public IReadOnlyList<BackupIssue> SkippedPaths { get; }
    public int UnchangedFiles { get; }
    public long TotalBytes { get; }
    internal IReadOnlyList<(string Source, string Destination)> CanonicalRoots { get; }

    internal MultiRestorePlan(List<BackupPlan> entries, List<(string Source, string Destination)> roots)
    {
        Entries = entries.AsReadOnly();
        CanonicalRoots = roots.AsReadOnly();
        Files = entries.SelectMany((entry, index) => entry.Files.Select(file =>
            file with { RelativePath = Prefix(index, file.RelativePath) })).ToList().AsReadOnly();
        SkippedPaths = entries.SelectMany((entry, index) => entry.SkippedPaths.Select(issue =>
            issue with { RelativePath = Prefix(index, issue.RelativePath) })).ToList().AsReadOnly();
        UnchangedFiles = entries.Sum(entry => entry.UnchangedFiles);
        TotalBytes = entries.Sum(entry => entry.TotalBytes);
    }

    internal static string Prefix(int index, string relative) => $"[{index + 1}] {relative}";
}

/// <summary>Restores explicitly paired folders in order after validating the entire reviewed batch.</summary>
public sealed class MultiRestoreEngine
{
    public Task<MultiRestorePlan> CreatePlanAsync(IReadOnlyList<RestoreFolderPair> pairs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var snapshot = pairs.ToArray();
        return Task.Run(() =>
        {
            var roots = ValidateMappings(snapshot, cancellationToken);
            var entries = new List<BackupPlan>();
            foreach (var pair in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(BackupEngine.CreatePlan(new BackupRequest(pair.SourcePath, pair.DestinationPath,
                    FileComparisonMode.Content), cancellationToken));
            }
            return new MultiRestorePlan(entries, roots);
        }, cancellationToken);
    }

    public Task<BackupResult> ExecuteAsync(MultiRestorePlan plan, IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default) => Task.Run(() => Execute(plan, progress, cancellationToken));

    private static BackupResult Execute(MultiRestorePlan plan, IProgress<BackupProgress>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var watch = Stopwatch.StartNew();
        var locks = new List<Mutex>();
        var errors = new List<BackupIssue>();
        var copied = 0;
        var completed = 0;
        long bytes = 0;
        var canceled = false;
        try
        {
            token.ThrowIfCancellationRequested();
            ValidatePlanMappings(plan, token);
            foreach (var name in plan.Entries.Select(entry => BackupEngine.GetDestinationLockName(entry.DestinationPath))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var mutex = new Mutex(false, name);
                var acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new IOException("この復元先では別のバックアップまたは復元を実行中です。");
                    locks.Add(mutex);
                }
                finally { if (!acquired) mutex.Dispose(); }
            }
            ValidatePlanMappings(plan, token);
            // No destination is touched until every row, including unchanged files,
            // has passed the original content snapshot checks under all target locks.
            for (var index = 0; index < plan.Entries.Count; index++)
            {
                var entry = plan.Entries[index];
                errors.AddRange(entry.SkippedPaths.Select(issue => PrefixIssue(index, issue)));
                errors.AddRange(BackupEngine.ValidateContentPlan(entry, token).Select(issue => PrefixIssue(index, issue)));
            }
            if (errors.Count > 0) return new BackupResult(0, 0, false, errors.AsReadOnly(), watch.Elapsed);

            for (var index = 0; index < plan.Entries.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var entry = plan.Entries[index];
                var row = index;
                var translated = progress is null ? null : new InlineProgress(value => progress.Report(new BackupProgress(
                    MultiRestorePlan.Prefix(row, value.RelativePath), completed + value.CompletedFiles,
                    plan.Files.Count, bytes + value.BytesCopied, plan.TotalBytes)));
                var result = BackupEngine.ExecuteUnderBatchLock(entry, translated, token);
                copied = checked(copied + result.CopiedFiles);
                bytes = checked(bytes + result.BytesCopied);
                completed = checked(completed + entry.Files.Count);
                errors.AddRange(result.Errors.Select(issue => PrefixIssue(row, issue)));
                if (result.IsCanceled) { canceled = true; break; }
                // A failed row invalidates the rest of this reviewed restore.
                if (result.Errors.Count > 0) break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled = true; }
        finally
        {
            for (var index = locks.Count - 1; index >= 0; index--)
            {
                locks[index].ReleaseMutex();
                locks[index].Dispose();
            }
        }
        return new BackupResult(copied, bytes, canceled, errors.AsReadOnly(), watch.Elapsed);
    }

    private static void ValidatePlanMappings(MultiRestorePlan plan, CancellationToken token)
    {
        var current = ValidateMappings(plan.Entries.Select(entry => new RestoreFolderPair(entry.SourcePath, entry.DestinationPath)).ToArray(), token);
        for (var index = 0; index < current.Count; index++)
        {
            if (!Same(current[index].Source, plan.CanonicalRoots[index].Source) ||
                !Same(current[index].Destination, plan.CanonicalRoots[index].Destination) ||
                plan.Entries[index].ComparisonMode != FileComparisonMode.Content)
                throw new IOException("差分確認後にフォルダーの対応が変わりました。もう一度差分を確認してください。");
        }
    }

    private static List<(string Source, string Destination)> ValidateMappings(IReadOnlyList<RestoreFolderPair> pairs, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        if (pairs.Count == 0) throw new ArgumentException("復元元と復元先を1組以上指定してください。");
        var roots = new List<(string Source, string Destination)>();
        foreach (var pair in pairs)
        {
            token.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(pair);
            var validated = BackupEngine.ValidateRoots(pair.SourcePath, pair.DestinationPath);
            roots.Add((BackupEngine.CanonicalizeExistingPrefix(validated.Source),
                BackupEngine.CanonicalizeExistingPrefix(validated.Destination)));
        }
        for (var index = 0; index < roots.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            for (var other = 0; other < roots.Count; other++)
            {
                if (Overlap(roots[index].Destination, roots[other].Source))
                    throw new ArgumentException("復元先には、いずれかの復元元と同じ場所や互いの配下を指定できません。");
                if (other >= index) continue;
                if (Overlap(roots[index].Destination, roots[other].Destination))
                    throw new ArgumentException("復元先が重複しています。同じ場所や互いの配下は指定できません。");
                if (Overlap(roots[index].Source, roots[other].Source))
                    throw new ArgumentException("復元元が重複しています。同じ場所や互いの配下は指定できません。");
            }
        }
        return roots;
    }

    private static BackupIssue PrefixIssue(int row, BackupIssue issue) => issue with { RelativePath = MultiRestorePlan.Prefix(row, issue.RelativePath) };
    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool Overlap(string left, string right) => BackupEngine.IsSameOrChild(left, right) || BackupEngine.IsSameOrChild(right, left);
    private sealed class InlineProgress(Action<BackupProgress> report) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => report(value);
    }
}
