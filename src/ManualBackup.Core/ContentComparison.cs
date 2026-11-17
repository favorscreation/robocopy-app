using System.Buffers;
using System.Security.Cryptography;

namespace ManualBackup.Core;

public sealed partial class BackupEngine
{
    private static FileSnapshot CaptureSnapshot(string path, bool allowMissing, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureNoReparseAncestors(path);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) when (allowMissing) { return new FileSnapshot(false, 0, default, null); }
        catch (DirectoryNotFoundException) when (allowMissing) { return new FileSnapshot(false, 0, default, null); }
        if ((attributes & FileAttributes.Directory) != 0)
            throw new IOException("ファイルと同名のフォルダーがあります。もう一度差分を確認してください。");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.SequentialScan);
        EnsureNoReparseAncestors(path);
        var information = new FileInfo(path);
        var length = input.Length;
        var time = information.LastWriteTimeUtc;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long readBytes = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
                readBytes += read;
            }
            information.Refresh();
            if (!information.Exists || length != readBytes || length != information.Length || time != information.LastWriteTimeUtc)
                throw new IOException("内容の確認中にファイルが変更されました。もう一度差分を確認してください。");
            return new FileSnapshot(true, length, time, Convert.ToHexString(hash.GetHashAndReset()));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void ValidateSnapshot(string path, FileSnapshot expected, CancellationToken token)
    {
        var actual = CaptureSnapshot(path, true, token);
        if (actual != expected)
            throw new IOException("差分確認後にファイルの有無・内容・更新日時が変わりました。上書きを中止しました。もう一度差分を確認してください。");
    }

    private static List<BackupIssue> ValidateContentPlan(BackupPlan plan, CancellationToken token)
    {
        var issues = new List<BackupIssue>();
        foreach (var directory in plan.Directories)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var source = CombineRelative(plan.SourcePath, directory);
                var destination = CombineRelative(plan.DestinationPath, directory);
                EnsureNoReparseAncestors(source);
                EnsureNoReparseAncestors(destination);
                if (!Directory.Exists(source) || File.Exists(destination))
                    throw new IOException("差分確認後にフォルダー構成が変わりました。もう一度差分を確認してください。");
            }
            catch (Exception exception) when (IsFileSystemError(exception))
            {
                issues.Add(new BackupIssue(directory, exception.Message));
            }
        }
        foreach (var (relative, state) in plan.ContentStates)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                ValidateSnapshot(CombineRelative(plan.SourcePath, relative), state.Source, token);
                ValidateSnapshot(CombineRelative(plan.DestinationPath, relative), state.Destination, token);
            }
            catch (Exception exception) when (IsFileSystemError(exception))
            {
                issues.Add(new BackupIssue(relative, exception.Message));
            }
        }
        return issues;
    }
}
