
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace HttpFileCache;

/// <summary>
/// HTTP-based file retreival, caching, and cache-management.
/// </summary>
public static class HttpFileCache
{
    /// <summary>
    /// Settings controlling HttpFileCache. If the defaults are not desired, this should
    /// be set only once at startup and not changed again during execution.
    /// </summary>
    public static HttpFileCacheConfiguration Configuration { get; set; } = new();

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

    // Pathname to the index.json file
    private static readonly string CacheIndexPathname;

    // Per docs and design, only one of these is used for all operations
    private static readonly HttpClient Downloader = new();

    // Static constructor resolves the default CacheLocation and ensures the index is loaded.
    static HttpFileCache()
    {
        if (Configuration.CacheLocation is null) Configuration.CacheLocation = Path.GetTempPath();

        CacheIndexPathname = Path.Combine(Configuration.CacheFullPath, "index.json");
        if (File.Exists(CacheIndexPathname)) ReadCacheIndex();
    }

    /// <summary>
    /// Retrieves a file from the cache or queues it for download. If no callback is provided
    /// is provided, the file will be cached without notification of completion or failure.
    /// </summary>
    public static void RequestUri(string sourceUri, int fileHandle = 0, Action<int, CachedFileData> callback = null)
    {
        (var request, var success) = PrepareRequest(sourceUri, fileHandle, callback);
        if(!success) return;
        _ = Task.Run(() => DownloadFile(request));
    }

    /// <summary>
    /// Retrieves a file from the cache or queues it for download. If no callback is provided
    /// is provided, the file will be cached without notification of completion or failure.
    /// </summary>
    public static async Task RequestUriAsync(string sourceUri, int fileHandle = 0, Action<int, CachedFileData> callback = null)
    {
        (var request, var success) = PrepareRequest(sourceUri, fileHandle, callback);
        if (!success) return;
        await DownloadFileAsync(request);
    }

    /// <summary>
    /// Retrieves a file from the cache or queues it for download. If no callback is provided
    /// is provided, the file will be cached without notification of completion or failure.
    /// </summary>
    public static async Task RequestUriAsync(string sourceUri, int fileHandle = 0, Func<int, CachedFileData, Task> callbackAsync = null)
    {
        (var request, var success) = await PrepareRequestAsync(sourceUri, fileHandle, callbackAsync);
        if (!success) return;
        await DownloadFileAsync(request);
    }

    /// <summary>
    /// File consumers should call this when disposing resources.
    /// </summary>
    public static void ReleaseUri(string sourceUri)
    {
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
    /// Although static classes cannot implement IDisposable, this should be called upon
    /// application shutdown to end any download activity in progress.
    /// </summary>
    public static void Dispose()
    {
        foreach (var kvp in QueuedURIs)
        {
            kvp.Value.CTS.Cancel();
        }
        QueuedURIs.Clear();
    }

    /// <summary>
    /// Prepares a request with a synchronous Action callback.
    /// </summary>
    private static (CachedFileRequest, bool) PrepareRequest(string sourceUri, int fileHandle = 0, Action<int, CachedFileData> callback = null)
    {
        var uri = ParseUri(sourceUri);
        if (uri is null)
        {
            callback?.Invoke(fileHandle, null);
            return (null, false);
        }

        // Immediate response if already cached; if file has expired, the
        // current version is still returned, but a new download is requested
        // and if the new copy is retrieved, the callback is invoked again.
        bool replacingExpiredFile = false;
        if (Index.TryGetValue(uri, out var fileData))
        {
            callback?.Invoke(fileHandle, fileData);

            // Mark it as in-use
            fileData.UsageCounter++;

            // Exit unless we also need to download a new copy
            var expires = fileData.RetrievalTimestamp.AddDays(Configuration.FileExpirationDays);
            if (Configuration.FileExpirationDays == 0 || DateTime.Now <= expires) return (null, false);

            replacingExpiredFile = true;
        }

        // Queue for retrieval
        var request = new CachedFileRequest
        {
            FileHandle = fileHandle,
            Callback = callback,
            ReplacingExpiredFile = replacingExpiredFile,
            Data = new CachedFileData
            {
                CacheFilename = new Guid().ToString().ToUpperInvariant(),
                OriginURI = uri
            }
        };
        QueuedURIs.Add(request.Data.OriginURI, request);

        return (request, true);
    }

    /// <summary>
    /// Prepares a request with a asynchronous Task function callback.
    /// </summary>
    private static async Task<(CachedFileRequest, bool)> PrepareRequestAsync(string sourceUri, int fileHandle = 0, Func<int, CachedFileData, Task> callbackAsync = null)
    {
        var uri = ParseUri(sourceUri);
        if (uri is null)
        {
            await callbackAsync?.Invoke(fileHandle, null);
            return (null, false);
        }

        // Immediate response if already cached; if file has expired, the
        // current version is still returned, but a new download is requested
        // and if the new copy is retrieved, the callback is invoked again.
        bool replacingExpiredFile = false;
        if (Index.TryGetValue(uri, out var fileData))
        {
            await callbackAsync?.Invoke(fileHandle, fileData);

            // Mark it as in-use
            fileData.UsageCounter++;

            // Exit unless we also need to download a new copy
            var expires = fileData.RetrievalTimestamp.AddDays(Configuration.FileExpirationDays);
            if (Configuration.FileExpirationDays == 0 || DateTime.Now <= expires) return (null, false);

            replacingExpiredFile = true;
        }

        // Queue for retrieval
        var request = new CachedFileRequest
        {
            FileHandle = fileHandle,
            CallbackAsync = callbackAsync,
            ReplacingExpiredFile = replacingExpiredFile,
            Data = new CachedFileData
            {
                CacheFilename = new Guid().ToString().ToUpperInvariant(),
                OriginURI = uri
            }
        };
        QueuedURIs.Add(request.Data.OriginURI, request);

        return (request, true);
    }

    /// <summary>
    /// Ensures the requested URI is valid. Returns the parsed version.
    /// </summary>
    private static string ParseUri(string sourceUri)
    {
        if(string.IsNullOrWhiteSpace(sourceUri)) throw new ArgumentNullException(nameof(sourceUri));

        var uri = (Configuration.CaseSensitivity) ? sourceUri : sourceUri.ToLowerInvariant();
        Uri parsedUri;
        try
        {
            parsedUri = new(sourceUri);
        }
        catch (Exception ex)
        {
            Configuration.Logger?.LogError($"{nameof(HttpFileCache)} unable to parse URI {sourceUri}\n{ex.Message}");
            return null;
        }
        return parsedUri.AbsoluteUri;
    }

    /// <summary>
    /// Synchronous file retrieval. This is executed on a background thread by RequestUri.
    /// </summary>
    private static void DownloadFile(CachedFileRequest request)
    {
        bool downloadFailed = false;

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
                    Index[request.Data.OriginURI] = request.Data;
                }

                CacheSpaceUsed += request.Data.Size;
                PruneCache();
                WriteCacheIndex();
            }
            else
            {
                downloadFailed = true;
            }

        }
        catch (OperationCanceledException)
        {
            downloadFailed = true;
            request.Callback?.Invoke(request.FileHandle, null);
        }
        catch (Exception)
        {
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
                    Index[request.Data.OriginURI] = request.Data;
                }

                CacheSpaceUsed += request.Data.Size;
                PruneCache();
                WriteCacheIndex();
            }
            else
            {
                downloadFailed = true;
            }
        }
        catch (OperationCanceledException)
        {
            downloadFailed = true;
            request.Callback?.Invoke(request.FileHandle, null);
        }
        catch (Exception)
        {
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
    /// Removes old content from the cache.
    /// </summary>
    private static void PruneCache()
    {
        if (CacheSpaceUsed < Configuration.SizeLimit) return;
        var files = Index
                    .Where(kvp => kvp.Value.UsageCounter == 0)
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
        var json = File.ReadAllText(CacheIndexPathname);
        Index = JsonConvert.DeserializeObject<Dictionary<string, CachedFileData>>(json);
        CacheSpaceUsed = Index.Sum(kvp => kvp.Value.Size);
    }

    /// <summary>
    /// Saves the cache index and re-calculates current cache size.
    /// </summary>
    private static void WriteCacheIndex()
    {
        string json = JsonConvert.SerializeObject(Index, Formatting.Indented);
        File.WriteAllText(CacheIndexPathname, json);
        CacheSpaceUsed = Index.Sum(kvp => kvp.Value.Size);
    }
}