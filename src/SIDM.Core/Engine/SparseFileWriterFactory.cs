using Microsoft.Extensions.Logging;
using SIDM.Core.Abstractions;

namespace SIDM.Core.Engine;

public sealed class SparseFileWriterFactory : IDownloadFileWriterFactory
{
    private readonly ILogger<SparseFileWriter> _writerLogger;

    public SparseFileWriterFactory(ILogger<SparseFileWriter> writerLogger)
    {
        _writerLogger = writerLogger;
    }

    public IDownloadFileWriter Allocate(string targetPath, long totalBytes) =>
        SparseFileWriter.Allocate(targetPath, totalBytes, _writerLogger);
}
