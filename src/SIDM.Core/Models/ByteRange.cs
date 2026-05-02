namespace SIDM.Core.Models;

/// <summary>
/// Inclusive byte range matching HTTP Range header semantics: <c>bytes=Start-End</c>
/// covers <c>End - Start + 1</c> bytes.
/// </summary>
public readonly record struct ByteRange(long Start, long End)
{
    public long Length => End - Start + 1;

    public static ByteRange FromLength(long start, long length)
    {
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start), "start must be non-negative");
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length), "length must be positive");
        return new ByteRange(start, start + length - 1);
    }

    public string ToHttpRangeValue() => $"bytes={Start}-{End}";
}
