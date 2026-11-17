using System.Diagnostics;
using System.Collections.Concurrent;

namespace ManualBackup.Core;

/// <summary>Backs up disjoint sources into explicit, separate folders beneath one destination.</summary>
public sealed class MultiBackupEngine
{
    public Task<MultiBackupPlan> CreatePlanAsync(MultiBackupRequest request, CancellationToken cancellationToken = default) =>
        Task.Run(() => CreatePlan(request, cancellationToken), cancellationToken);

    public Task<BackupResult> ExecuteAsync(MultiBackupPlan plan, IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        // A dedicated coordinator owns the mutexes until every bounded worker
        // finishes. Blocking the coordinator cannot starve thread-pool workers.
        Task.Factory.StartNew(() => Execute(plan, progress, cancellationToken), CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

    private static MultiBackupPlan CreatePlan(MultiBackupRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var validated = ValidateRequest(request);
        var entries = new List<BackupSourcePlan>();
        foreach (var source in validated.Sources)
        {
            token.ThrowIfCancellationRequested();
            var plan = BackupEngine.CreatePlan(new BackupRequest(source.Source.SourcePath, source.DestinationPath), token);
            entries.Add(new BackupSourcePlan(source.Source, plan));
        }
        return new MultiBackupPlan(validated.DestinationPath, validated.CanonicalDestinationPath, entries,
            validated.Sources.Select(source => source.CanonicalSourcePath).ToList(),
            validated.Sources.Select(source => source.CanonicalTargetPath).ToList(), request.MaxParallelSources);
    }

    private static BackupResult Execute(MultiBackupPlan plan, IProgress<BackupProgress>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var watch = Stopwatch.StartNew();
        if (token.IsCancellationRequested)
            return new BackupResult(0, 0, true, Array.Empty<BackupIssue>(), watch.Elapsed);
        var validation = ValidatePlanMappings(plan);
        var heldLocks = new List<Mutex>();
        var copied = 0;
        long bytes = 0;
        var canceled = false;
        var errors = new List<BackupIssue>();
        try
        {
            // Lock every target before the first write; an independent operation on
            // a later child target must not cause a late collision after partial work.
            var lockPaths = new[] { validation.DestinationPath }
                .Concat(validation.Sources.Select(source => source.DestinationPath));
            var lockNames = lockPaths.Select(BackupEngine.GetDestinationLockName)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            foreach (var name in lockNames)
            {
                token.ThrowIfCancellationRequested();
                var mutex = new Mutex(false, name);
                var acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new IOException("この保存先では別のバックアップまたは復元を実行中です。");
                    heldLocks.Add(mutex);
                }
                finally { if (!acquired) mutex.Dispose(); }
            }
            // Re-check mappings after the locks and validate every later source
            // before creating even the first destination directory.
            _ = ValidatePlanMappings(plan);
            ValidateAllPlans(plan, token);

            var results = ExecuteWorkers(plan, progress, token);
            foreach (var result in results.Results)
            {
                copied = checked(copied + result.CopiedFiles);
                bytes = checked(bytes + result.BytesCopied);
                errors.AddRange(result.Errors);
            }
            errors.AddRange(results.WorkerErrors);
            canceled = token.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled = true; }
        finally
        {
            for (var i = heldLocks.Count - 1; i >= 0; i--)
            {
                heldLocks[i].ReleaseMutex();
                heldLocks[i].Dispose();
            }
        }
        return new BackupResult(copied, bytes, canceled, errors.AsReadOnly(), watch.Elapsed);
    }

    private static WorkerBatchResult ExecuteWorkers(MultiBackupPlan plan, IProgress<BackupProgress>? progress,
        CancellationToken userToken)
    {
        using var workCancellation = CancellationTokenSource.CreateLinkedTokenSource(userToken);
        var workToken = workCancellation.Token;
        var queueGate = new object();
        var progressGate = new object();
        var nextSource = 0;
        var results = new BackupResult?[plan.Entries.Count];
        var slots = new SourceProgress[plan.Entries.Count];
        var workerErrors = new ConcurrentQueue<BackupIssue>();
        var workers = new List<Task>();
        var completedFiles = 0;
        long reportedBytes = 0;
        var observerFailed = false;

        void ReportProgress(int index, string relativePath, int completed, long bytesCopied)
        {
            if (progress is null) return;
            lock (progressGate)
            {
                var entry = plan.Entries[index];
                ref var slot = ref slots[index];
                var nextCompleted = Math.Max(slot.CompletedFiles, Math.Clamp(completed, 0, entry.Plan.Files.Count));
                var nextBytes = Math.Max(slot.BytesCopied, Math.Clamp(bytesCopied, 0, entry.Plan.TotalBytes));
                completedFiles += Math.Min(nextCompleted - slot.CompletedFiles, plan.Files.Count - completedFiles);
                reportedBytes += Math.Min(nextBytes - slot.BytesCopied, plan.TotalBytes - reportedBytes);
                slot.CompletedFiles = nextCompleted;
                slot.BytesCopied = nextBytes;
                if (!string.IsNullOrEmpty(relativePath)) slot.RelativePath = relativePath;
                if (observerFailed) return;
                try
                {
                    // Serialize observer calls as well as snapshots, so callers
                    // never receive an older aggregate after a newer one.
                    progress.Report(new BackupProgress(MultiBackupPlan.Prefix(entry.Source.TargetFolderName, relativePath),
                        completedFiles, plan.Files.Count, reportedBytes, plan.TotalBytes));
                }
                catch (Exception exception)
                {
                    observerFailed = true;
                    workerErrors.Enqueue(new BackupIssue(MultiBackupPlan.Prefix(entry.Source.TargetFolderName, relativePath),
                        "進捗の通知を続行できませんでした: " + exception.Message));
                    workCancellation.Cancel();
                }
            }
        }

        void FinishProgress(int index, BackupResult result)
        {
            if (progress is null) return;
            int completed;
            string relative;
            lock (progressGate)
            {
                completed = result.IsCanceled ? slots[index].CompletedFiles : plan.Entries[index].Plan.Files.Count;
                relative = slots[index].RelativePath ?? "";
            }
            ReportProgress(index, relative, completed, result.BytesCopied);
        }

        void RunWorker()
        {
            try
            {
                while (true)
                {
                    int index;
                    // Queue claiming is independent of the progress lock: a slow
                    // observer cannot prevent the other bounded workers starting.
                    lock (queueGate)
                    {
                        if (workToken.IsCancellationRequested || nextSource >= plan.Entries.Count) return;
                        index = nextSource++;
                    }
                    if (workToken.IsCancellationRequested) return;
                    var entry = plan.Entries[index];
                    var timer = Stopwatch.StartNew();
                    var translated = progress is null ? null : new InlineProgress<BackupProgress>(value =>
                        ReportProgress(index, value.RelativePath, value.CompletedFiles, value.BytesCopied));
                    BackupResult result;
                    try { result = BackupEngine.ExecuteUnderBatchLock(entry.Plan, translated, workToken); }
                    catch (OperationCanceledException) when (workToken.IsCancellationRequested)
                    {
                        result = new BackupResult(0, 0, true, Array.Empty<BackupIssue>(), timer.Elapsed);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
                    {
                        result = new BackupResult(0, 0, false,
                            new[] { new BackupIssue("", exception.Message) }, timer.Elapsed);
                    }
                    results[index] = result with
                    {
                        Errors = result.Errors.Select(issue => issue with
                        {
                            RelativePath = MultiBackupPlan.Prefix(entry.Source.TargetFolderName, issue.RelativePath)
                        }).ToList().AsReadOnly()
                    };
                    FinishProgress(index, result);
                }
            }
            catch (Exception exception)
            {
                // Never let an unexpected dedicated-worker exception escape while
                // another worker could still be copying under coordinator locks.
                workerErrors.Enqueue(new BackupIssue("", "並列コピー処理を続行できませんでした: " + exception.Message));
                workCancellation.Cancel();
            }
        }

        Exception? coordinationError = null;
        try
        {
            var count = Math.Min(plan.MaxParallelSources, plan.Entries.Count);
            for (var index = 0; index < count; index++)
            {
                if (workToken.IsCancellationRequested) break;
                workers.Add(Task.Factory.StartNew(RunWorker, CancellationToken.None,
                    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default));
            }
        }
        catch (Exception exception)
        {
            coordinationError = exception;
            workCancellation.Cancel();
        }
        finally
        {
            // This also covers Task/thread creation failing partway through.
            // Join on the coordinator's original thread before it can unlock.
            foreach (var worker in workers)
            {
                try { worker.GetAwaiter().GetResult(); }
                catch (Exception exception)
                {
                    coordinationError ??= exception;
                    workCancellation.Cancel();
                }
            }
        }
        if (coordinationError is not null)
            workerErrors.Enqueue(new BackupIssue("", "並列コピー処理を開始・完了できませんでした: " + coordinationError.Message));
        return new WorkerBatchResult(results.OfType<BackupResult>().ToArray(), workerErrors.ToArray());
    }

    private static ValidatedRequest ValidatePlanMappings(MultiBackupPlan plan)
    {
        var result = ValidateRequest(new MultiBackupRequest(plan.Entries.Select(entry => entry.Source).ToArray(),
            plan.DestinationPath, plan.MaxParallelSources));
        if (!SamePath(result.CanonicalDestinationPath, plan.CanonicalDestinationPath))
            throw new IOException("差分確認後に保存先の場所が変わりました。もう一度差分を確認してください。");
        for (var i = 0; i < result.Sources.Count; i++)
        {
            var source = result.Sources[i];
            var entry = plan.Entries[i];
            if (!SamePath(source.CanonicalSourcePath, plan.CanonicalSourcePaths[i]) ||
                !SamePath(source.CanonicalTargetPath, plan.CanonicalTargetPaths[i]) ||
                !SamePath(source.Source.SourcePath, entry.Plan.SourcePath) ||
                !SamePath(source.DestinationPath, entry.Plan.DestinationPath) ||
                entry.Plan.ComparisonMode != FileComparisonMode.SizeAndTimestamp)
                throw new IOException("差分確認後にフォルダーの対応が変わりました。もう一度差分を確認してください。");
        }
        return result;
    }

    private static void ValidateAllPlans(MultiBackupPlan plan, CancellationToken token)
    {
        foreach (var entry in plan.Entries)
        {
            foreach (var relative in entry.Plan.Directories)
            {
                token.ThrowIfCancellationRequested();
                var source = BackupEngine.CombineRelative(entry.Plan.SourcePath, relative);
                var destination = BackupEngine.CombineRelative(entry.Plan.DestinationPath, relative);
                BackupEngine.EnsureNoReparseAncestors(source);
                BackupEngine.EnsureNoReparseAncestors(destination);
                if (!Directory.Exists(source)) throw new DirectoryNotFoundException("差分確認後にコピー元のフォルダーがなくなりました。");
                if (File.Exists(destination)) throw new IOException("保存先のフォルダーと同名のファイルがあります。");
            }
            foreach (var file in entry.Plan.Files)
            {
                token.ThrowIfCancellationRequested();
                BackupEngine.EnsureNoReparseAncestors(file.SourcePath);
                BackupEngine.EnsureNoReparseAncestors(file.DestinationPath);
                var information = new FileInfo(file.SourcePath);
                if (!information.Exists || information.Length != file.Length || information.LastWriteTimeUtc != file.LastWriteTimeUtc)
                    throw new IOException("差分確認後にコピー元のファイルが変わりました。もう一度差分を確認してください。");
                if (Directory.Exists(file.DestinationPath)) throw new IOException("保存先のファイルと同名のフォルダーがあります。");
            }
        }
    }

    private static ValidatedRequest ValidateRequest(MultiBackupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaxParallelSources is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(request.MaxParallelSources), "同時に実行するコピー元の数は1から8で指定してください。");
        if (request.Sources is null || request.Sources.Count == 0)
            throw new ArgumentException("コピー元フォルダーを1件以上指定してください。", nameof(request));
        var sources = request.Sources.ToArray();
        var destination = BackupEngine.Normalize(request.DestinationPath, "保存先");
        BackupEngine.EnsureNoReparseAncestors(destination);
        if (File.Exists(destination)) throw new ArgumentException("保存先にはフォルダーを指定してください。");
        var canonicalDestination = BackupEngine.CanonicalizeExistingPrefix(destination);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var validated = new List<ValidatedSource>();
        foreach (var source in sources)
        {
            if (source is null) throw new ArgumentException("コピー元の設定が不正です。");
            var alias = ValidateTargetName(source.TargetFolderName, sources.Length == 1);
            if (!aliases.Add(alias)) throw new ArgumentException("保存先のサブフォルダー名が重複しています。別々の名前を指定してください。");
            // Check against the whole common destination, not just this row's child.
            var roots = BackupEngine.ValidateRoots(source.SourcePath, destination);
            var target = string.IsNullOrEmpty(alias) ? destination : Path.Combine(destination, alias);
            _ = BackupEngine.ValidateRoots(roots.Source, target);
            var canonicalSource = BackupEngine.CanonicalizeExistingPrefix(roots.Source);
            var canonicalTarget = BackupEngine.CanonicalizeExistingPrefix(target);
            foreach (var previous in validated)
            {
                if (Overlap(canonicalSource, previous.CanonicalSourcePath))
                    throw new ArgumentException("コピー元には同じ場所や互いの配下を重複して指定できません。");
                if (Overlap(canonicalTarget, previous.CanonicalTargetPath))
                    throw new ArgumentException("保存先のサブフォルダーが同じ場所を指しています。別々の名前を指定してください。");
            }
            validated.Add(new ValidatedSource(new BackupSource(roots.Source, alias), target, canonicalSource, canonicalTarget));
        }
        return new ValidatedRequest(destination, canonicalDestination, validated.AsReadOnly());
    }

    private static string ValidateTargetName(string name, bool allowBlank)
    {
        if (name is null) throw new ArgumentException("保存先のサブフォルダー名が不正です。");
        if (string.IsNullOrWhiteSpace(name))
        {
            if (allowBlank) return "";
            throw new ArgumentException("複数のコピー元には、それぞれ保存先のサブフォルダー名を指定してください。");
        }
        if (name.Length > 255 || name is "." or ".." || Path.IsPathRooted(name) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\') ||
            name.EndsWith('.') || name.EndsWith(' '))
            throw new ArgumentException("保存先のサブフォルダー名には、区切り文字・末尾の空白・ピリオドなどを使用できません。");
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (BackupEngine.IsDosShortNameComponent(name))
            throw new ArgumentException("保存先のサブフォルダー名に「ABCDEF~1」のようなWindowsの短い別名は指定できません。通常の名前を指定してください。");
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
             "123456789¹²³".Contains(stem[3])))
            throw new ArgumentException("保存先のサブフォルダー名にWindowsの予約名は使用できません。");
        return name;
    }

    private static bool SamePath(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private static bool Overlap(string first, string second) => BackupEngine.IsSameOrChild(first, second) || BackupEngine.IsSameOrChild(second, first);
    private sealed record ValidatedSource(BackupSource Source, string DestinationPath, string CanonicalSourcePath, string CanonicalTargetPath);
    private sealed record ValidatedRequest(string DestinationPath, string CanonicalDestinationPath, IReadOnlyList<ValidatedSource> Sources);
    private sealed record WorkerBatchResult(IReadOnlyList<BackupResult> Results, IReadOnlyList<BackupIssue> WorkerErrors);
    private struct SourceProgress
    {
        public int CompletedFiles;
        public long BytesCopied;
        public string? RelativePath;
    }
    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
