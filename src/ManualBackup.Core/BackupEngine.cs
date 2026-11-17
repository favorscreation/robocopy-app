using System.Buffers;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ManualBackup.Core;

/// <summary>Copies added/changed files only. It never removes source or destination files.</summary>
public sealed partial class BackupEngine
{
    private const int BufferSize = 1024 * 1024;

    public Task<BackupPlan> CreatePlanAsync(BackupRequest request, CancellationToken cancellationToken = default) =>
        Task.Run(() => CreatePlan(request, cancellationToken), cancellationToken);

    public Task<BackupResult> ExecuteAsync(BackupPlan plan, IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        // A named mutex must be acquired and released by the same thread.
        Task.Run(() => Execute(plan, progress, cancellationToken));

    internal static BackupPlan CreatePlan(BackupRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.ComparisonMode))
            throw new ArgumentException("比較方法が不正です。", nameof(request));
        var (source, destination) = ValidateRoots(request.SourcePath, request.DestinationPath);
        var files = new List<BackupFile>();
        var directories = new List<string>();
        var skipped = new List<BackupIssue>();
        var unchanged = 0;
        var contentStates = new Dictionary<string, ContentFileState>(StringComparer.OrdinalIgnoreCase);
        var destinationTargets = new List<(string RelativePath, string TargetPath)>();
        var destinationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var needsCanonicalTargetCheck = false;
        void TrackDestination(string relative, string target)
        {
            if (!destinationNames.Add(target))
                throw new ArgumentException("複数のコピー元の名前が同じ保存先を指しています。名前を変更してください。");
            destinationTargets.Add((relative, target));
            needsCanonicalTargetCheck |= relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(IsDosShortNameComponent);
        }
        var pending = new Stack<string>();
        pending.Push(source);

        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            var relativeDirectory = Path.GetRelativePath(source, directory);
            if (relativeDirectory == ".") relativeDirectory = "";
            try
            {
                EnsureNoReparseAncestors(directory);
                var destinationDirectory = CombineRelative(destination, relativeDirectory);
                EnsureNoReparseAncestors(destinationDirectory);
                if (File.Exists(destinationDirectory))
                    throw new IOException("保存先に同名のファイルがあり、フォルダーを作成できません。");
                TrackDestination(relativeDirectory, destinationDirectory);
                directories.Add(relativeDirectory);
                // Materialize inside the try so enumeration errors are reported, not hidden.
                var entries = new DirectoryInfo(directory).GetFileSystemInfos();
                Array.Sort(entries, (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
                foreach (var entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(source, entry.FullName);
                    try
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            skipped.Add(new BackupIssue(relative, "リンク・ジャンクション・クラウドプレースホルダーは対象外です。"));
                            continue;
                        }
                        if ((entry.Attributes & FileAttributes.Directory) != 0)
                        {
                            pending.Push(entry.FullName);
                            continue;
                        }
                        var sourceFile = (FileInfo)entry;
                        var targetPath = CombineRelative(destination, relative);
                        EnsureNoReparseAncestors(targetPath);
                        if (Directory.Exists(targetPath))
                            throw new IOException("保存先に同名のフォルダーがあります。");
                        TrackDestination(relative, targetPath);
                        var destinationFile = new FileInfo(targetPath);
                        var length = sourceFile.Length;
                        var lastWriteTimeUtc = sourceFile.LastWriteTimeUtc;
                        var destinationExists = destinationFile.Exists;
                        bool identical;
                        if (request.ComparisonMode == FileComparisonMode.Content)
                        {
                            var sourceSnapshot = CaptureSnapshot(sourceFile.FullName, false, token);
                            var destinationSnapshot = CaptureSnapshot(targetPath, true, token);
                            contentStates.Add(relative, new ContentFileState(sourceSnapshot, destinationSnapshot));
                            length = sourceSnapshot.Length;
                            lastWriteTimeUtc = sourceSnapshot.LastWriteTimeUtc;
                            destinationExists = destinationSnapshot.Exists;
                            identical = destinationExists && sourceSnapshot == destinationSnapshot;
                        }
                        else
                        {
                            identical = destinationExists && length == destinationFile.Length &&
                                lastWriteTimeUtc == destinationFile.LastWriteTimeUtc;
                        }
                        if (identical)
                        {
                            unchanged++;
                            continue;
                        }
                        files.Add(new BackupFile(relative, sourceFile.FullName, targetPath,
                            length, lastWriteTimeUtc,
                            destinationExists ? BackupChangeKind.Updated : BackupChangeKind.New));
                    }
                    catch (Exception exception) when (IsFileSystemError(exception))
                    {
                        skipped.Add(new BackupIssue(relative, exception.Message));
                    }
                }
            }
            catch (Exception exception) when (IsFileSystemError(exception))
            {
                skipped.Add(new BackupIssue(relativeDirectory, exception.Message));
            }
        }
        if (needsCanonicalTargetCheck)
        {
            // A literal DOS-looking source name can address an existing long
            // destination name's automatic 8.3 alias. Include unchanged files
            // and directories so two source names cannot silently share a target.
            EnsureDistinctCanonicalTargets(destinationTargets.Select(target => target.TargetPath), token);
        }
        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        directories.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a, b));
        return new BackupPlan(source, destination, files, directories, unchanged, skipped,
            request.ComparisonMode, contentStates, needsCanonicalTargetCheck);
    }

    internal static BackupResult Execute(BackupPlan plan, IProgress<BackupProgress>? progress, CancellationToken token)
        => ExecuteCore(plan, progress, token, acquireDestinationLock: true);

    // Only the batch coordinator calls this after owning all root/target mutexes.
    // Worker threads must not acquire a mutex owned by that coordinator thread.
    internal static BackupResult ExecuteUnderBatchLock(BackupPlan plan, IProgress<BackupProgress>? progress, CancellationToken token)
        => ExecuteCore(plan, progress, token, acquireDestinationLock: false);

    private static BackupResult ExecuteCore(BackupPlan plan, IProgress<BackupProgress>? progress,
        CancellationToken token, bool acquireDestinationLock)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var watch = Stopwatch.StartNew();
        var errors = new List<BackupIssue>();
        var copiedFiles = 0;
        var completedFiles = 0;
        long copiedBytes = 0;
        var canceled = false;
        if (token.IsCancellationRequested)
            return new BackupResult(0, 0, true, errors.AsReadOnly(), watch.Elapsed);

        ValidateRoots(plan.SourcePath, plan.DestinationPath);
        using var destinationLock = acquireDestinationLock ? new Mutex(false, GetDestinationLockName(plan.DestinationPath)) : null;
        var lockHeld = false;
        try
        {
            if (acquireDestinationLock)
            {
                try { lockHeld = destinationLock!.WaitOne(0); }
                catch (AbandonedMutexException) { lockHeld = true; }
                if (!lockHeld) throw new IOException("この保存先には別のバックアップ処理が実行されています。");
            }
            if (plan.ComparisonMode == FileComparisonMode.Content)
            {
                // Refuse the complete restore before creating directories or staging files
                // when any reviewed source/target (including unchanged files) has changed.
                errors.AddRange(ValidateContentPlan(plan, token));
                if (errors.Count > 0)
                    return new BackupResult(0, 0, false, errors.AsReadOnly(), watch.Elapsed);
            }
            foreach (var relativeDirectory in plan.Directories)
            {
                token.ThrowIfCancellationRequested();
                var sourceDirectory = CombineRelative(plan.SourcePath, relativeDirectory);
                var destinationDirectory = CombineRelative(plan.DestinationPath, relativeDirectory);
                try
                {
                    EnsureNoReparseAncestors(sourceDirectory);
                    if (!Directory.Exists(sourceDirectory))
                        throw new DirectoryNotFoundException("確認後にコピー元のフォルダーがなくなりました。もう一度差分を確認してください。");
                    EnsureNoReparseAncestors(destinationDirectory);
                    Directory.CreateDirectory(destinationDirectory);
                    EnsureNoReparseAncestors(destinationDirectory);
                }
                catch (Exception exception) when (IsFileSystemError(exception))
                {
                    errors.Add(new BackupIssue(relativeDirectory, exception.Message));
                }
            }

            if (plan.NeedsCanonicalTargetCheck)
            {
                // Creating a long directory may create a DOS alias which makes
                // a later literal short-looking directory refer to that same tree.
                // Detect this before writing any file, including distinct leaves.
                try
                {
                    EnsureDistinctCanonicalTargets(plan.Directories.Select(relative => CombineRelative(plan.DestinationPath, relative))
                        .Concat(plan.Files.Select(file => file.DestinationPath)), token);
                }
                catch (ArgumentException exception)
                {
                    errors.Add(new BackupIssue("", exception.Message));
                    return new BackupResult(0, 0, false, errors.AsReadOnly(), watch.Elapsed);
                }
            }

            foreach (var file in plan.Files)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    plan.ContentStates.TryGetValue(file.RelativePath, out var contentState);
                    CopyOne(file, plan.SourcePath, plan.DestinationPath, contentState, token,
                        bytes => progress?.Report(new BackupProgress(file.RelativePath, completedFiles,
                            plan.Files.Count, copiedBytes + bytes, plan.TotalBytes)));
                    copiedFiles++;
                    copiedBytes += file.Length;
                }
                catch (Exception exception) when (IsFileSystemError(exception))
                {
                    errors.Add(new BackupIssue(file.RelativePath, exception.Message));
                }
                completedFiles++;
                progress?.Report(new BackupProgress(file.RelativePath, completedFiles,
                    plan.Files.Count, copiedBytes, plan.TotalBytes));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled = true; }
        catch (Exception exception) when (!acquireDestinationLock)
        {
            // Keep already committed counts when a batch worker encounters an
            // unexpected failure. Public standalone/restore behavior is unchanged.
            errors.Add(new BackupIssue("", "コピー処理を続行できませんでした: " + exception.Message));
        }
        finally
        {
            if (lockHeld) destinationLock!.ReleaseMutex();
        }
        return new BackupResult(copiedFiles, copiedBytes, canceled, errors.AsReadOnly(), watch.Elapsed);
    }

    private static void CopyOne(BackupFile file, string sourceRoot, string destinationRoot,
        ContentFileState? contentState, CancellationToken token, Action<long> reportBytes)
    {
        // Validate the public plan's paths again immediately before any writes.
        if (!string.Equals(file.SourcePath, CombineRelative(sourceRoot, file.RelativePath), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(file.DestinationPath, CombineRelative(destinationRoot, file.RelativePath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("バックアップ対象のパスが無効です。");
        EnsureNoReparseAncestors(file.SourcePath);
        EnsureNoReparseAncestors(file.DestinationPath);
        var sourceInfo = new FileInfo(file.SourcePath);
        if (!sourceInfo.Exists || sourceInfo.Length != file.Length || sourceInfo.LastWriteTimeUtc != file.LastWriteTimeUtc)
            throw new IOException("差分確認後にコピー元が変更されました。もう一度差分を確認してください。");
        if (contentState is not null)
            ValidateSnapshot(file.DestinationPath, contentState.Destination, token);

        var parent = Path.GetDirectoryName(file.DestinationPath)!;
        Directory.CreateDirectory(parent);
        EnsureNoReparseAncestors(parent);
        var temporaryPath = Path.Combine(parent, ".manualbackup-" + Guid.NewGuid().ToString("N") + ".tmp");
        byte[]? buffer = null;
        var replacementOwnsTemporary = false;
        try
        {
            // ShareRead prevents normal Windows writers from modifying this file while it is copied.
            using (var input = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                       BufferSize, FileOptions.SequentialScan))
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       BufferSize, FileOptions.SequentialScan))
            using (var contentHash = contentState is not null ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null)
            {
                if (input.Length != file.Length)
                    throw new IOException("コピー元のサイズが変更されました。");
                buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                long currentBytes = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var read = input.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    output.Write(buffer, 0, read);
                    contentHash?.AppendData(buffer, 0, read);
                    currentBytes += read;
                    reportBytes(currentBytes);
                }
                output.Flush(flushToDisk: true);
                sourceInfo.Refresh();
                if (currentBytes != file.Length || sourceInfo.LastWriteTimeUtc != file.LastWriteTimeUtc)
                    throw new IOException("コピー中にコピー元が変更されました。既存のバックアップは保持されました。");
                if (contentHash is not null && Convert.ToHexString(contentHash.GetHashAndReset()) != contentState!.Source.Sha256)
                    throw new IOException("差分確認後に復元元の内容が変更されました。もう一度差分を確認してください。");
            }
            token.ThrowIfCancellationRequested();
            File.SetLastWriteTimeUtc(temporaryPath, file.LastWriteTimeUtc);
            EnsureNoReparseAncestors(file.SourcePath);
            EnsureNoReparseAncestors(file.DestinationPath);
            if (contentState is not null)
                ValidateSnapshot(file.DestinationPath, contentState.Destination, token);
            token.ThrowIfCancellationRequested();
            if (contentState is not null && contentState.Destination.Exists)
            {
                replacementOwnsTemporary = true;
                ReplaceReviewedFile(temporaryPath, file, contentState, token);
            }
            else
                File.Move(temporaryPath, file.DestinationPath,
                    overwrite: contentState is null && file.Kind == BackupChangeKind.Updated);
        }
        finally
        {
            if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
            // Only this invocation's staging file can be removed, never an existing backup.
            try { if (!replacementOwnsTemporary) File.Delete(temporaryPath); }
            catch (Exception exception) when (IsFileSystemError(exception)) { }
        }
    }

    // File.Replace can fail after renaming the original. Keep a same-directory
    // original backup until commit succeeds, and never retry an uncertain state.
    internal static void ReplaceReviewedFile(string temporaryPath, BackupFile file, ContentFileState state,
        CancellationToken token, Action<string, string, string>? replace = null)
    {
        var originalBackup = Path.Combine(Path.GetDirectoryName(file.DestinationPath)!,
            ".manualbackup-original-" + Guid.NewGuid().ToString("N") + ".bak");
        var committed = false;
        var retainStaging = false;
        replace ??= static (staging, destination, original) => File.Replace(staging, destination, original, ignoreMetadataErrors: true);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                EnsureNoReparseAncestors(file.SourcePath);
                EnsureNoReparseAncestors(file.DestinationPath);
                EnsureNoReparseAncestors(temporaryPath);
                EnsureNoReparseAncestors(originalBackup);
                ValidateSnapshot(file.SourcePath, state.Source, token);
                ValidateSnapshot(file.DestinationPath, state.Destination, token);
                ValidateSnapshot(temporaryPath, state.Source, token);
                if (EntryExists(originalBackup))
                    throw new IOException("元ファイルの退避先が既に存在するため、置換を中止しました。");
                try
                {
                    replace(temporaryPath, file.DestinationPath, originalBackup);
                    committed = true;
                    return;
                }
                catch (IOException exception)
                {
                    var code = exception.HResult & 0xffff;
                    // 32/33/1175 retain both original names. 1176/1177 can
                    // change names and must be recovered rather than retried.
                    if (!OperatingSystem.IsWindows() || attempt >= 3 || code is not (32 or 33 or 1175) || EntryExists(originalBackup))
                        throw;
                    if (token.WaitHandle.WaitOne((attempt + 1) * 100)) token.ThrowIfCancellationRequested();
                }
            }
        }
        catch (Exception exception) when (IsFileSystemError(exception))
        {
            retainStaging = (exception.HResult & 0xffff) is 1176 or 1177;
            // Repair an absent destination from our own verified original only.
            // A new destination wins the race; no recovery operation overwrites it.
            try
            {
                EnsureNoReparseAncestors(originalBackup);
                EnsureNoReparseAncestors(file.DestinationPath);
                if (EntryExists(originalBackup) && !EntryExists(file.DestinationPath))
                {
                    ValidateSnapshot(originalBackup, state.Destination, CancellationToken.None);
                    File.Move(originalBackup, file.DestinationPath, overwrite: false);
                }
            }
            catch (Exception recoveryException) when (IsFileSystemError(recoveryException)) { }
            var retained = new List<string>();
            // Include known recovery paths even when permissions prevent inspection.
            try { if (EntryExists(originalBackup)) retained.Add(originalBackup); }
            catch (Exception inspectionException) when (IsFileSystemError(inspectionException)) { retained.Add(originalBackup); }
            if (retainStaging) retained.Add(temporaryPath);
            if (retained.Count > 0)
                throw new IOException(exception.Message + " 回復用ファイルを保持しました: " + string.Join(" / ", retained), exception);
            throw;
        }
        finally
        {
            // A failed replacement must never discard its original backup.
            if (committed)
            {
                try { File.Delete(originalBackup); }
                catch (Exception exception) when (IsFileSystemError(exception)) { }
            }
            if (!retainStaging)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception exception) when (IsFileSystemError(exception)) { }
            }
        }
    }

    private static bool EntryExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static (string Source, string Destination) ValidateRoots(string sourcePath, string destinationPath)
    {
        var source = Normalize(sourcePath, "コピー元");
        var destination = Normalize(destinationPath, "保存先");
        EnsureNoReparseAncestors(source);
        EnsureNoReparseAncestors(destination);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("コピー元のフォルダーが見つかりません。");
        if (File.Exists(destination)) throw new ArgumentException("保存先にはフォルダーを指定してください。");
        // Short (8.3) names and mapped-drive aliases must not bypass overlap checks.
        var physicalSource = CanonicalizeExistingPrefix(source);
        var physicalDestination = CanonicalizeExistingPrefix(destination);
        if (IsSameOrChild(physicalSource, physicalDestination) || IsSameOrChild(physicalDestination, physicalSource))
            throw new ArgumentException("コピー元と保存先は、同じフォルダーや互いの配下にできません。");
        return (source, destination);
    }

    internal static string Normalize(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException(label + "には絶対パスのフォルダーを指定してください。");
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal) || path.StartsWith("\\\\.\\", StringComparison.Ordinal))
            throw new ArgumentException(label + "には通常のフォルダーパスを指定してください。");
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(fullPath)!;
        foreach (var component in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (component.EndsWith(' ') || component.EndsWith('.') || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException(label + "のパスに利用できない文字や末尾の空白・ピリオドがあります。");
        }
        return fullPath;
    }

    internal static bool IsSameOrChild(string candidate, string parent) =>
        string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    internal static string CombineRelative(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new IOException("相対パスが無効です。");
        var combined = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsSameOrChild(combined, root)) throw new IOException("フォルダーの外を指すパスは利用できません。");
        return combined;
    }

    internal static void EnsureNoReparseAncestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("リンク・ジャンクション・クラウドプレースホルダーを含むパスは利用できません。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            var parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
    }

    private static bool IsFileSystemError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    internal static string CanonicalizeExistingPrefix(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        var suffix = new Stack<string>();
        var existing = path;
        while (!Directory.Exists(existing) && !File.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent)) return path;
            suffix.Push(Path.GetFileName(existing));
            existing = parent;
        }
        using var handle = CreateFile(existing, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException("フォルダーの実体を確認できません。", new Win32Exception(Marshal.GetLastWin32Error()));
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        }
        if (length == 0)
            throw new IOException("フォルダーの実体を確認できません。", new Win32Exception(Marshal.GetLastWin32Error()));
        var canonical = buffer.ToString();
        if (canonical.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) canonical = "\\\\" + canonical[8..];
        else if (canonical.StartsWith("\\\\?\\", StringComparison.Ordinal)) canonical = canonical[4..];
        while (suffix.TryPop(out var component)) canonical = Path.Combine(canonical, component);
        return Path.TrimEndingDirectorySeparator(canonical);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path,
        uint pathLength, uint flags);

    internal static string GetDestinationLockName(string destinationPath) =>
        "Global\\ManualBackup_" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(CanonicalizeExistingPrefix(destinationPath).ToUpperInvariant())));

    internal static bool IsDosShortNameComponent(string name)
    {
        var period = name.IndexOf('.');
        var stem = period < 0 ? name : name[..period];
        var extension = period < 0 ? "" : name[(period + 1)..];
        var tilde = stem.IndexOf('~');
        return tilde is >= 1 and <= 6 && stem.Length <= 8 && tilde < stem.Length - 1 &&
            stem[(tilde + 1)..].All(character => character is >= '0' and <= '9') &&
            !stem[..tilde].Any(character => character is ' ' or '.' or '~') &&
            extension.Length <= 3 && !extension.Any(character => character is ' ' or '.' or '~');
    }

    private static void EnsureDistinctCanonicalTargets(IEnumerable<string> targets, CancellationToken token)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            if (!identities.Add(CanonicalizeExistingPrefix(target)))
                throw new ArgumentException("保存先で短い別名（8.3形式）が別のファイルやフォルダーと同じ場所を指しています。別の保存先または名前を指定してください。");
        }
    }
}
