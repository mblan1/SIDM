namespace SIDM.Core.Abstractions;

public interface IDownloadFileWriterFactory
{
    IDownloadFileWriter Allocate(string targetPath, long totalBytes);
}
