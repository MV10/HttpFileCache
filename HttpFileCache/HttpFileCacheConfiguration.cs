
using Microsoft.Extensions.Logging;

namespace HttpFileCache;

/// <summary>
/// 
/// </summary>
public class HttpFileCacheConfiguration
{
	public string CacheLocation { get; set; } = null;

	public string CacheDirectory { get; set; } = "http_cache";

	public long SizeLimit { get; set; } = 512;

	public int FileExpirationDays { get; set; } = 30;

	public bool CaseSensitivity { get; set; } = false;

	public ILogger Logger { get; set; } = null;

	public bool UseExpiredFiles { get; set; } = true;
}
