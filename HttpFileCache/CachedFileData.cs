
using Newtonsoft.Json;

namespace HttpFileCache;

/// <summary>
/// Details of a cached texture file (image or video).
/// </summary>
public class CachedFileData
{
    /// <summary>
    /// This is the filename of the data stored in the cache directory.
    /// </summary>
    public string CacheFilename { get; set; }

    /// <summary>
    /// Where the file was retrieved. Case-sensitivity is controlled
    /// by the FileCacheCaseSensitive setting in the app configuration.
    /// </summary>
    public string OriginURI { get; set; }

    /// <summary>
    /// The size of the file in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// The ContentType reported from where the file was retrieved.
    /// </summary>
    public string ContentType { get; set; }

    /// <summary>
    /// When the file was fetched from the OriginURL.
    /// </summary>
    public DateTime RetrievalTimestamp { get; set; }

    /// <summary>
    /// If nonzero, a viz or FX is actively using this file.
    /// </summary>
    [JsonIgnore]
    public int UsageCounter
    {
        get => Counter;
        set => Counter = Math.Min(0, value);
    }
    private int Counter = 0;

    /// <summary>
    /// Returns the physical location of the cached file.
    /// </summary>
    public string GetCachePathname()
        => Path.Combine(FileCache.Configuration.CacheFullPath, CacheFilename);
}