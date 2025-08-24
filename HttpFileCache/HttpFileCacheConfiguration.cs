
using Microsoft.Extensions.Logging;

namespace HttpFileCache;

/// <summary>
/// Settings for HttpFileCache.
/// </summary>
public class HttpFileCacheConfiguration
{
	/// <summary>
	/// The parent directory of the cache directory. If null, this will be
	/// changed to the user's local temp directory the first time HttpFileCache
	/// is referenced or used.
	/// </summary>
	public string CacheLocation { get; set; } = null;

	/// <summary>
	/// The name of the directory (inside CacheLocation) where the cached files
	/// and the index is stored.
	/// </summary>
	public string CacheDirectory { get; set; } = "http_cache";

	/// <summary>
	/// Maximum combined size of all cached files in megabytes. When the cache
	/// size is exceeded, existing files are purged in order by retrieval age.
	/// </summary>
	public long SizeLimit { get; set; } = 512;

	/// <summary>
	/// Maximum age of a file before a new copy is downloaded.
	/// </summary>
	public int FileExpirationDays { get; set; } = 30;

	/// <summary>
	/// When true, all requested URIs are stored as lowercase. Cached files
	/// are indexed according to URI, so if your target server is case-sensitive,
	/// change this to true to avoid collisions.
	/// </summary>
	public bool CaseSensitivity { get; set; } = false;

	/// <summary>
	/// An optional log sink.
	/// </summary>
	public ILogger Logger { get; set; } = null;

	/// <summary>
	/// When true, if an expired file is found in the cache for a requested URI,
	/// the expired version will be returned (via callback) while a new copy is
	/// downloaded. When the new copy is ready the callback will be invoked again.
	/// </summary>
	public bool UseExpiredFiles { get; set; } = true;

	/// <summary>
	/// Returns a pathname which combines CacheLocation and CacheDirectory.
	/// </summary>
	public string CacheFullPath { get => Path.Combine(CacheLocation, CacheDirectory); }
}
