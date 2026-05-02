namespace SIDM.Core.Models;

public enum DownloadStatus
{
    Queued = 0,
    Probing = 1,
    Downloading = 2,
    Paused = 3,
    Completed = 4,
    Failed = 5,
    Canceled = 6,
}

public enum SegmentStatus
{
    Pending = 0,
    Active = 1,
    Completed = 2,
    Failed = 3,
}

public enum SourceKind
{
    Direct = 0,
    Hls = 1,
    Dash = 2,
    YouTube = 3,
}

public enum DownloadPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3,
}
