namespace CustomBrowser.Models
{
    public enum DownloadType
    {
        Direct,
        Stream,
        Ytdl
    }

    public enum DownloadStatus
    {
        Queued,
        Downloading,
        Paused,
        Completed,
        Failed
    }
}

