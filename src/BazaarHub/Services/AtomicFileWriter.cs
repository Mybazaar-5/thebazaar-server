using System.Text;

namespace BazaarHub.Services;

/// <summary>
/// D7-P2-05：全仓统一的防碰撞原子写盘器。
/// 每次写入均使用 `${targetPath}.{PID}.{Guid:N}.tmp` 唯一随机临时后缀，异常时自动清理临时文件，
/// 彻底消除并发写入或双开实例共享固定 `.tmp` 后缀引发的冲突与残留。
/// </summary>
public static class AtomicFileWriter
{
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string BuildUniqueTempPath(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        return $"{targetPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
    }

    public static void WriteAllTextAtomic(string targetPath, string content, Encoding? encoding = null)
    {
        WriteWithTempFile(targetPath, tmp => File.WriteAllText(tmp, content ?? string.Empty, encoding ?? Utf8NoBom));
    }

    public static void WriteAllBytesAtomic(string targetPath, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        WriteWithTempFile(targetPath, tmp => File.WriteAllBytes(tmp, bytes));
    }

    public static void CopyFileAtomic(string sourcePath, string targetPath)
    {
        WriteWithTempFile(targetPath, tmp => File.Copy(sourcePath, tmp, overwrite: true));
    }

    public static void WriteWithTempFile(string targetPath, Action<string> writeAction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(writeAction);

        string? dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        string tmpPath = BuildUniqueTempPath(targetPath);
        try
        {
            writeAction(tmpPath);
            File.Move(tmpPath, targetPath, overwrite: true);
        }
        catch
        {
            TryDeleteQuietly(tmpPath);
            throw;
        }
    }

    private static void TryDeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 忽略清理异常，保留原始业务异常
        }
    }
}
