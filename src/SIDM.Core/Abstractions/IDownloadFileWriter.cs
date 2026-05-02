namespace SIDM.Core.Abstractions;

/// <summary>
/// Owns the on-disk file for one download. Implementations pre-allocate a sparse file,
/// accept lockless concurrent writes from multiple segment workers, and finalize by
/// moving the temp file to its target name.
/// </summary>
public interface IDownloadFileWriter : IAsyncDisposable
{
    string TempFilePath { get; }
    string TargetFilePath { get; }
    long TotalBytes { get; }

    /// <summary>
    /// Writes <paramref name="buffer"/> at absolute file offset <paramref name="offset"/>.
    /// Safe to call concurrently from multiple workers as long as their byte ranges
    /// do not overlap.
    /// </summary>
    ValueTask WriteAtAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>
    /// Closes the file handle and renames the temp file to its target name.
    /// Returns the final path (which may differ from <see cref="TargetFilePath"/> if
    /// a collision policy renamed it).
    /// </summary>
    Task<string> FinalizeAsync(CancellationToken cancellationToken);
}
