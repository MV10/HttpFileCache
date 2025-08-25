
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace HttpFileCache;

/// <summary>
/// HTTP-based file retreival, caching, and cache-management.
/// </summary>
public static class FileCache
{
    /// <summary>
    /// Settings controlling HttpFileCache. If the defaults are not desired, this should
    /// be set only once at startup and not changed again during execution.
    /// </summary>
    public static FileCacheConfiguration Configuration { get; set; } = new();

    /// <summary>
    /// Details of all the files in the cache. The dictionary is keyed on download URIs.
    /// </summary>
    public static IReadOnlyDictionary<string, CachedFileData> CacheIndex { get => Index; }

    /// <summary>
    /// Current size of cache contents in megabytes (approximate).
    /// </summary>
    public static long CacheSize { get => CacheSpaceUsed / 1024; }

    // Keyed on FileCacheData OriginURI
    private static Dictionary<string, CachedFileData> Index = new();
    private static Dictionary<string, CachedFileRequest> QueuedURIs = new();

    // Total of all FileCacheData.Size values
    private static long CacheSpaceUsed;

    // Configuration size limit in bytes (MB * 1024)
    private static long CacheMaxSize;

    // Pathname to the index.json file
    private static string CacheIndexPathname;

    // Per docs and design, only one of these is used for all operations
    private static readonly HttpClient Downloader = new();

    // Throws if cache is used without init
    private static bool Initialized = false;

    /// <summary>
    /// This must be called before use.
    /// </summary>
    public static void Initialize()
    {
        if (string.IsNullOrWhiteSpace(Configuration.CacheLocation)) Configuration.CacheLocation = Path.GetTempPath();
        if (!Directory.Exists(Configuration.CacheFullPath)) Directory.CreateDirectory(Configuration.CacheFullPath);
        CacheIndexPathname = Path.Combine(Configuration.CacheFullPath, "index.json");
        CacheMaxSize = Configuration.SizeLimit * 1024;
        ReadCacheIndex();
        foreach (var kvp in Index) kvp.Value.UsageCounter = 0;
        Initialized = true;

        Configuration.Logger?.LogTrace($"Constructor completed");
    }

    /// <summary>
    /// Retrieves a file from the cache or queues it for download. If no callback is provided
    /// is provided, the file will be cached without notification of completion or failure.
    /// </summary>
    public static void RequestFile(string sourceUri, int fileHandle = 0, Action<int, CachedFileData> callback = null)
    {
        if (!Initialized) throw new InvalidOperationException("Invoke FileCache.Initialize before use");

        Configuration.Logger?.LogDebug($"{nameof(RequestFile)} URI {sourceUri}");

        var uri = ParseUri(sourceUri);
        if (uri is null)
        {
            callback?.Invoke(fileHandle, null);
            return;
        }

        // Immediate response if already cached; if file has expired, the
        // current version is still returned, but a new download is requested
        // and if the new copy is retrieved, the callback is invoked again.
        bool replacingExpiredFile = false;
        long expiredFileSize = 0;
        if (Index.TryGetValue(uri, out var fileData))
        {
            Configuration.Logger?.LogDebug($"{nameof(RequestFile)} found in cache as {uri} @ {fileData.RetrievalTimestamp}");
            callback?.Invoke(fileHandle, fileData);

            // Mark it as in-use
            fileData.UsageCounter++;

            // Exit unless we also need to download a new copy
            var expires = fileData.RetrievalTimestamp.AddDays(Configuration.FileExpirationDays);
            if (Configuration.FileExpirationDays == 0 || DateTime.Now <= expires) return;

            expiredFileSize = fileData.Size;
            replacingExpiredFile = true;
        }

        // Queue for retrieval
        var request = new CachedFileRequest
        {
            FileHandle = fileHandle,
            Callback = callback,
            ReplacingExpiredFile = replacingExpiredFile,
            ExpiredFileSize = expiredFileSize,
            Data = new CachedFileData
            {
                CacheFilename = Guid.NewGuid().ToString().ToUpperInvariant(),
                OriginURI = uri
            }
        };
        QueuedURIs.Add(request.Data.OriginURI, request);

        _ = Task.Run(() => DownloadFile(request));
    }

    /// <summary>
    /// Retrieves a file from the cache or queues it for download. If no callback is provided
    /// is provided, the file will be cached without notification of completion or failure.
    /// If the consumer requires a callback, specify callback or callbackAsync explicitly.
    /// </summary>
    public static async Task RequestFileAsync(string sourceUri, int fileHandle = 0, Action<int, CachedFileData> callback = null, Func<int, CachedFileData, Task> callbackAsync = null)
    {
        if (!Initialized) throw new InvalidOperationException("Invoke FileCache.Initialize before use");
        if (callback is not null && callbackAsync is not null) throw new ArgumentException("Only one of callback or callbackAsync may be specified");

        Configuration.Logger?.LogDebug($"{nameof(RequestFileAsync)} URI {sourceUri}");

        var uri = ParseUri(sourceUri);
        if (uri is null)
        {
            callback?.Invoke(fileHandle, null);
            await (callbackAsync?.Invoke(fileHandle, null) ?? Task.CompletedTask); // sigh https://stackoverflow.com/a/33592569/152997
            return;
        }

        // Immediate response if already cached; if file has expired, the
        // current version is still returned, but a new download is requested
        // and if the new copy is retrieved, the callback is invoked again.
        bool replacingExpiredFile = false;
        long expiredFileSize = 0;
        if (Index.TryGetValue(uri, out var fileData))
        {
            Configuration.Logger?.LogDebug($"{nameof(RequestFileAsync)} found in cache as {uri} @ {fileData.RetrievalTimestamp}");
            callback?.Invoke(fileHandle, fileData);
            await (callbackAsync?.Invoke(fileHandle, fileData) ?? Task.CompletedTask); // sigh https://stackoverflow.com/a/33592569/152997

            // Mark it as in-use
            fileData.UsageCounter++;

            // Exit unless we also need to download a new copy
            var expires = fileData.RetrievalTimestamp.AddDays(Configuration.FileExpirationDays);
            if (Configuration.FileExpirationDays == 0 || DateTime.Now <= expires) return;

            expiredFileSize = fileData.Size;
            replacingExpiredFile = true;
        }

        // Queue for retrieval
        var request = new CachedFileRequest
        {
            FileHandle = fileHandle,
            Callback = callback,
            CallbackAsync = callbackAsync,
            ReplacingExpiredFile = replacingExpiredFile,
            ExpiredFileSize = expiredFileSize,
            Data = new CachedFileData
            {
                CacheFilename = Guid.NewGuid().ToString().ToUpperInvariant(),
                OriginURI = uri
            }
        };
        QueuedURIs.Add(request.Data.OriginURI, request);

        await DownloadFileAsync(request);
    }

    /// <summary>
    /// File consumers should call this when disposing resources.
    /// </summary>
    public static void ReleaseFile(string sourceUri)
    {
        if (!Initialized) throw new InvalidOperationException("Invoke FileCache.Initialize before use");

        Configuration.Logger?.LogDebug($"{nameof(ReleaseFile)} URI {sourceUri}");

        var uri = ParseUri(sourceUri);
        if (uri is null) return;

        if (QueuedURIs.TryGetValue(uri, out var download))
        {
            download.CTS.Cancel();
            QueuedURIs.Remove(uri);
        }

        if (Index.TryGetValue(uri, out var file))
        {
            file.UsageCounter--;
        }
    }

    /// <summary>
    /// Removes the specified file from the cache (if present).
    /// </summary>
    public static void DeleteFile(string sourceUri)
    {
        if (!Initialized) throw new InvalidOperationException("Invoke FileCache.Initialize before use");

        Configuration.Logger?.LogDebug($"{nameof(DeleteFile)} URI {sourceUri}");

        var uri = ParseUri(sourceUri);
        if(Index.TryGetValue(uri, out var file))
        {
            File.Delete(file.GetCachePathname());
            Index.Remove(uri);
            WriteCacheIndex();
        }
    }

    /// <summary>
    /// Removes all files from the cache.
    /// </summary>
    public static void ClearCache()
    {
        if (!Initialized) throw new InvalidOperationException("Invoke FileCache.Initialize before use");

        Configuration.Logger?.LogDebug(nameof(ClearCache));

        foreach (var kvp in Index)
        {
            File.Delete(kvp.Value.GetCachePathname());
        }
        Index.Clear();
        WriteCacheIndex();
    }

    /// <summary>
    /// Ensures the requested URI is valid. Returns the parsed version.
    /// </summary>
    public static string ParseUri(string sourceUri)
    {
        if (!Initialized) throw new InvalidOperationException("Invoke FileCache.Initialize before use");

        Configuration.Logger?.LogDebug($"{nameof(ParseUri)} URI {sourceUri}");

        if (string.IsNullOrWhiteSpace(sourceUri)) throw new ArgumentNullException(nameof(sourceUri));

        var uri = (Configuration.CaseSensitivity) ? sourceUri : sourceUri.ToLowerInvariant();
        Uri parsedUri;
        try
        {
            parsedUri = new(sourceUri);
        }
        catch (Exception ex)
        {
            Configuration.Logger?.LogError($"Unable to parse URI {sourceUri}\n{ex.Message}");
            return null;
        }
        return parsedUri.AbsoluteUri;
    }

    /// <summary>
    /// Although static classes cannot implement IDisposable, this should be called upon
    /// application shutdown to end any download activity in progress.
    /// </summary>
    public static void Dispose()
    {
        Configuration.Logger?.LogDebug(nameof(Dispose));

        foreach (var kvp in QueuedURIs)
        {
            kvp.Value.CTS.Cancel();
        }
        QueuedURIs.Clear();
    }

    /// <summary>
    /// Synchronous file retrieval. This is executed on a background thread by RequestUri.
    /// </summary>
    private static void DownloadFile(CachedFileRequest request)
    {
        bool downloadFailed = false;

        Configuration.Logger?.LogDebug($"{nameof(DownloadFile)} URI {request.Data.OriginURI}");

        try
        {
            request.CTS.Token.ThrowIfCancellationRequested();

            var requestMessage = new HttpRequestMessage(HttpMethod.Get, request.Data.OriginURI);
            var response = Downloader.Send(requestMessage, request.CTS.Token);
            if (response.IsSuccessStatusCode)
            {
                using var stream = response.Content.ReadAsStream();
                using var fileStream = new FileStream(request.Data.GetCachePathname(), FileMode.Create, FileAccess.Write);
                byte[] buffer = new byte[8192];
                int bytesRead;
                while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    request.CTS.Token.ThrowIfCancellationRequested();
                    fileStream.Write(buffer, 0, bytesRead);
                    request.Data.Size += bytesRead;
                }

                request.Data.RetrievalTimestamp = DateTime.Now;
                request.Data.ContentType = response.Content.Headers.ContentType?.ToString();

                request.Callback?.Invoke(request.FileHandle, request.Data);

                if (request.ReplacingExpiredFile)
                {
                    File.Delete(Index[request.Data.OriginURI].GetCachePathname());
                    CacheSpaceUsed -= request.ExpiredFileSize;
                    Index[request.Data.OriginURI] = request.Data;
                }
                else
                {
                    Index.Add(request.Data.OriginURI, request.Data);
                    CacheSpaceUsed += request.Data.Size;
                }

                Configuration.Logger?.LogDebug($"{nameof(DownloadFile)} completed URI {request.Data.OriginURI}");
                PruneCache(request.Data.OriginURI);
                WriteCacheIndex();
            }
            else
            {
                Configuration.Logger?.LogError($"{nameof(DownloadFile)} download failed, HTTP status {response.StatusCode} {response.ReasonPhrase}");
                downloadFailed = true;
            }

        }
        catch (OperationCanceledException)
        {
            Configuration.Logger?.LogError($"{nameof(DownloadFile)} download failed, cancellation token triggered");
            downloadFailed = true;
            request.Callback?.Invoke(request.FileHandle, null);
        }
        catch (Exception ex)
        {
            Configuration.Logger?.LogError($"{nameof(DownloadFile)} download failed, {ex.GetType()}: {ex.Message}");
            downloadFailed = true;
            request.Callback?.Invoke(request.FileHandle, null);
        }
        finally
        {
            QueuedURIs.Remove(request.Data.OriginURI);
        }

        // clean up failed partial download
        if (downloadFailed && File.Exists(request.Data.GetCachePathname()))
        {
            File.Delete(request.Data.GetCachePathname());
        }
    }

    /// <summary>
    /// Async file retrieval. Executed by RequestUriAsync.
    /// </summary>
    private static async Task DownloadFileAsync(CachedFileRequest request)
    {
        bool downloadFailed = false;

        Configuration.Logger?.LogDebug($"{nameof(DownloadFileAsync)} URI {request.Data.OriginURI}");

        try
        {
            request.CTS.Token.ThrowIfCancellationRequested();

            var requestMessage = new HttpRequestMessage(HttpMethod.Get, request.Data.OriginURI);
            var response = await Downloader.SendAsync(requestMessage, request.CTS.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var fileStream = new FileStream(request.Data.GetCachePathname(), FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
                byte[] buffer = new byte[8192];
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, request.CTS.Token).ConfigureAwait(false)) > 0)
                {
                    request.CTS.Token.ThrowIfCancellationRequested();
                    await fileStream.WriteAsync(buffer, 0, bytesRead, request.CTS.Token).ConfigureAwait(false);
                    request.Data.Size += bytesRead;
                }

                request.Data.RetrievalTimestamp = DateTime.Now;
                request.Data.ContentType = response.Content.Headers.ContentType?.ToString();

                request.Callback?.Invoke(request.FileHandle, request.Data);

                if (request.ReplacingExpiredFile)
                {
                    File.Delete(Index[request.Data.OriginURI].GetCachePathname());
                    CacheSpaceUsed -= request.ExpiredFileSize;
                    Index[request.Data.OriginURI] = request.Data;
                }
                else
                {
                    Index.Add(request.Data.OriginURI, request.Data);
                    CacheSpaceUsed += request.Data.Size;
                }

                Configuration.Logger?.LogDebug($"{nameof(DownloadFileAsync)} completed URI {request.Data.OriginURI}");
                PruneCache(request.Data.OriginURI);
                WriteCacheIndex();
            }
            else
            {
                Configuration.Logger?.LogError($"{nameof(DownloadFileAsync)} download failed, HTTP status {response.StatusCode} {response.ReasonPhrase}");
                downloadFailed = true;
            }
        }
        catch (OperationCanceledException)
        {
            Configuration.Logger?.LogError($"{nameof(DownloadFileAsync)} download failed, cancellation token triggered");
            downloadFailed = true;
            request.Callback?.Invoke(request.FileHandle, null);
        }
        catch (Exception ex)
        {
            Configuration.Logger?.LogError($"{nameof(DownloadFileAsync)} download failed, {ex.GetType()}: {ex.Message}");
            downloadFailed = true;
            request.Callback?.Invoke(request.FileHandle, null);
        }
        finally
        {
            QueuedURIs.Remove(request.Data.OriginURI);
        }

        if (downloadFailed && File.Exists(request.Data.GetCachePathname()))
        {
            File.Delete(request.Data.GetCachePathname());
        }
    }

    /// <summary>
    /// Removes old content from the cache. If protectedUri is provided,
    /// it will not be excluded (typically this is the file that was just
    /// downloaded that triggered the purge).
    /// </summary>
    private static void PruneCache(string protectedUri = null)
    {
        if (CacheSpaceUsed < CacheMaxSize) return;

        Configuration.Logger?.LogDebug(nameof(PruneCache));

        var files = Index
                    .Where(kvp => kvp.Value.UsageCounter == 0 && !kvp.Value.OriginURI.Equals(protectedUri) )
                    .OrderBy(kvp => kvp.Value.RetrievalTimestamp)
                    .Select(kvp => kvp.Value)
                    .ToList();

        foreach (var file in files)
        {
            if (File.Exists(file.GetCachePathname()))
            {
                File.Delete(file.GetCachePathname());
                CacheSpaceUsed -= file.Size;
                Index.Remove(file.OriginURI);
            }
            if (CacheSpaceUsed < Configuration.SizeLimit) return;
        }
    }

    /// <summary>
    /// Loads the cache index and calculates current cache size.
    /// </summary>
    private static void ReadCacheIndex()
    {
        if (!File.Exists(CacheIndexPathname)) return;
        Configuration.Logger?.LogDebug(nameof(ReadCacheIndex));
        var json = File.ReadAllText(CacheIndexPathname);
        Index = JsonConvert.DeserializeObject<Dictionary<string, CachedFileData>>(json);
        CacheSpaceUsed = Index.Sum(kvp => kvp.Value.Size);
    }

    /// <summary>
    /// Saves the cache index and re-calculates current cache size.
    /// </summary>
    private static void WriteCacheIndex()
    {
        Configuration.Logger?.LogDebug(nameof(WriteCacheIndex));
        string json = JsonConvert.SerializeObject(Index, Formatting.Indented);
        File.WriteAllText(CacheIndexPathname, json);
        CacheSpaceUsed = Index.Sum(kvp => kvp.Value.Size);
    }
}