
namespace HttpFileCache;

/// <summary>
/// Represents a resource being downloaded.
/// </summary>
public class CachedFileRequest
{
    public int FileHandle;

    public Action<int, CachedFileData> Callback;

    public CancellationTokenSource CTS = new();

    public bool ReplacingExpiredFile = false;

    public CachedFileData Data;
}