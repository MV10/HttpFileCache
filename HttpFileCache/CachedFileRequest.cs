
namespace HttpFileCache;

/// <summary>
/// Represents a resource being downloaded.
/// </summary>
public class CachedFileRequest
{
    /// <summary>
    /// Details of the cached file to be returned via callback.
    /// </summary>
    public CachedFileData Data;

    /// <summary>
    /// Optional identifier provided by the library consumer.
    /// </summary>
    public int FileHandle;

    /// <summary>
    /// When true, an expired file for the same URI already exists.
    /// </summary>
    public bool ReplacingExpiredFile = false;

    /// <summary>
    /// The size of the file to be replaced.
    /// </summary>
    public long ExpiredFileSize = 0;

    /// <summary>
    /// Optional blocking callback when a cached file is already
    /// present, or a requested download has been completed.
    /// </summary>
    public Action<int, CachedFileData> Callback;

    /// <summary>
    /// Optional awaited callback when a cached file is already
    /// present, or a requested download has been completed.
    /// </summary>
    public Func<int, CachedFileData, Task> CallbackAsync;

    /// <summary>
    /// DO NOT USE. Managed internally by HttpFileCache.
    /// </summary>
    public CancellationTokenSource CTS = new();
}