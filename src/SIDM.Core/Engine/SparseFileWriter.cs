using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using SIDM.Core.Abstractions;

namespace SIDM.Core.Engine;

/// <summary>
/// NTFS-friendly sparse-file writer. Pre-allocates a <c>.sidmpart</c> file of the
/// expected total size, then accepts lockless concurrent writes at byte offsets via
/// <see cref="RandomAccess.WriteAsync(Microsoft.Win32.SafeHandles.SafeFileHandle, System.ReadOnlyMemory{byte}, long, System.Threading.CancellationToken)"/>.
/// On finalize, renames to the target path with a collision-safe policy.
/// </summary>
public sealed class SparseFileWriter : IDownloadFileWriter
{
    public const string TempSuffix = ".sidmpart";

    private readonly ILogger<SparseFileWriter> _logger;
    private readonly SafeFileHandle _handle;
    private readonly object _disposeLock = new();
    private bool _disposed;
    private bool _finalized;

    public string TempFilePath { get; }
    public string TargetFilePath { get; }
    public long TotalBytes { get; }

    private SparseFileWriter(SafeFileHandle handle, string tempPath, string targetPath, long totalBytes, ILogger<SparseFileWriter> logger)
    {
        _handle = handle;
        TempFilePath = tempPath;
        TargetFilePath = targetPath;
        TotalBytes = totalBytes;
        _logger = logger;
    }

    public static SparseFileWriter Allocate(string targetPath, long totalBytes, ILogger<SparseFileWriter> logger)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("targetPath required", nameof(targetPath));
        if (totalBytes <= 0) throw new ArgumentOutOfRangeException(nameof(totalBytes), "totalBytes must be positive");

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = targetPath + TempSuffix;

        // .NET's preallocationSize only applies to FileMode.Create — it tells the
        // filesystem to reserve contiguous space up front. For resume we must use
        // OpenOrCreate without preallocation, then SetLength to enforce the size.
        var isResume = File.Exists(tempPath);
        var handle = isResume
            ? File.OpenHandle(
                tempPath,
                mode: FileMode.OpenOrCreate,
                access: FileAccess.ReadWrite,
                share: FileShare.Read,
                options: FileOptions.Asynchronous | FileOptions.RandomAccess)
            : File.OpenHandle(
                tempPath,
                mode: FileMode.Create,
                access: FileAccess.ReadWrite,
                share: FileShare.Read,
                options: FileOptions.Asynchronous | FileOptions.RandomAccess,
                preallocationSize: totalBytes);

        try
        {
            RandomAccess.SetLength(handle, totalBytes);
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        logger.LogDebug("Allocated sparse file {Path} of {Bytes} bytes", tempPath, totalBytes);
        return new SparseFileWriter(handle, tempPath, targetPath, totalBytes, logger);
    }

    public ValueTask WriteAtAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        if (offset < 0 || offset >= TotalBytes)
            throw new ArgumentOutOfRangeException(nameof(offset), $"offset {offset} outside [0, {TotalBytes})");
        if (offset + buffer.Length > TotalBytes)
            throw new ArgumentException($"write of {buffer.Length} bytes at offset {offset} exceeds total {TotalBytes}");

        if (_disposed) throw new ObjectDisposedException(nameof(SparseFileWriter));
        return RandomAccess.WriteAsync(_handle, buffer, offset, cancellationToken);
    }

    public async Task<string> FinalizeAsync(CancellationToken cancellationToken)
    {
        if (_finalized) return TargetFilePath;

        // Flush + close before move.
        try
        {
            RandomAccess.FlushToDisk(_handle);
        }
        catch (NotSupportedException)
        {
            // Some filesystems don't support flush; ignore.
        }

        lock (_disposeLock)
        {
            if (!_disposed)
            {
                _handle.Dispose();
                _disposed = true;
            }
        }

        var finalPath = ResolveCollision(TargetFilePath);
        File.Move(TempFilePath, finalPath, overwrite: false);
        _finalized = true;

        _logger.LogInformation("Finalized download to {Path}", finalPath);
        await Task.CompletedTask;
        return finalPath;
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            if (!_disposed)
            {
                _handle.Dispose();
                _disposed = true;
            }
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// If <paramref name="path"/> already exists, append <c> (n)</c> before the extension
    /// until a free name is found. Mirrors how browsers name colliding downloads.
    /// </summary>
    public static string ResolveCollision(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException($"Could not find non-colliding filename for {path}");
    }
}

