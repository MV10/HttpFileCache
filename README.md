# HttpFileCache [![NuGet](https://img.shields.io/nuget/v/HttpFileCache.svg)](https://nuget.org/packages/HttpFileCache)

This .NET library supports simple HTTP file retrieval and caching. It was created to support image and video downloads for my [Monkey Hi Hat](https://github.com/MV10/monkey-hi-hat) music visualizer, but the functionality seems generally useful so I decided to share the code as a library package instead.

You can configure the storage location (or use the default, which is the user's local temp directory), maximum storage size in megabytes, and the maximum file age in days, after which the given URI will be refreshed when requested. A small command-line utility is included for manual cache inspection and management.

Because this was created for internal use by a project I fully controlled, there are certain safety features I didn't bother to implement. For example, you _could_ change the configuration object once the cache is in use, and you'd probably break things. The rule of thumb is, don't do stupid things, and stupid things won't happen. If you need something but it seems like it might have negative repercussions, just open an Issue and ask me about it.

> Normally I don't do the "pre-release" thing with my packages, but the sheer variability of usage patterns and data with caching means I'm not comfortable declaring this one-dot-zero ready-for-primetime just yet.

# Usage

The library is implemented as a simple static class. Cached files are stored with an opaque GUID filename tracked in a simple JSON index. The index is exposed as a read-only dictionary keyed on the file origin URI. Several other data elements are available such as the current size of the cache.

> The cache is _not_ suitable for simultaneous use by more than one application.

## Configuration

To start, create a `FileCacheConfiguration` object and assign it to the `FileCache.Configuration` property, then invoke the `Initialize` method. An exception will be thrown if cache methods are invoked without initialization.

```csharp
// These values represent the default settings.
FileCache.Configuration = new
{
	CacheLocation = null,			// use the default local temp directory
	CacheDirectory = "http_cache",	// directory name inside CacheLocation
	SizeLimit = 512,				// maximum cache size in megabytes
	FileExpirationDays = 30,		// files will be refreshed after this
	UseExpiredFiles = true,    		// temporary access to expired files
	CaseSensitivity = false,		// false is safer if you don't need it
	LoggerFactory = null,			// a Microsoft.Extensions.Logging factory
};

// Call this after setting config, but before using anything else.
FileCache.Initialize();
```

The maximum cache size in `SizeLimit` is specified in megabytes. You should avoid setting `SizeLimit` to small values, or values you know are very close to the size of the files you are retrieving. The limit can be exceeded by exactly one file -- that most recently requested. But more generally, constantly thrashing the cache with removals defeats the purpose.

When `UseExpiredFiles" is true, if a file URI is requested and an expired version is already in the cache, a callback will be invoked immediately providing your application with a reference to the expired content, which can be used while a replacement is retrieved. When the replacement is available, the callback will be invoked again and the consumer must immediately stop using the expired content, as it will be removed when the callback returns. Of course, if the file is not in the cache, a download is enqueued and the callback is invoked once the file is available. Callbacks are optional but for this reason consumers should use them if expired content can be served. (Callbacks are optional because it's useful for utilities, scripts that pre-load the cache, and other scenarios where the caller won't actually use the content.)

URI case sensitivity can be tricky. According to the standards, only the protocol (HTTP or HTTPS) and the domain name are case insensitive. In practice, most URIs are fully case insensitive, although non-Windows servers can be picky about this, and some systems intentionally use case sensitivity as part of their identifier schemes (YouTube is a major example). In the application for which HttpFileCache was designed, end-users will specify URIs in config files, and so it is safest to disable case sensitivity by default. On the other hand, if case sensitivity is enabled, the worst that will happen is you may end up with multiple copies of the file in the cache.

The `Logger` property is a standard `ILogger` object as defined by _Microsoft.Extensions.Logging_ (which can be provided by other libraries such as _Serilog_).

The `UseExpiredFiles` setting is explained in more detail below, but it determines whether the client application will receive a known-expired file to use while a new copy is being retrieved.

## File Operations

HttpFileCache has operations to request files and downloads, abort downloads, and manage the cache in general.

### Requesting a File

File retrieval consists of sending the file URI, a unique integer identifier generated and maintaned by your application, and a callback function which receives the integer identifier and a `CachedFileData` object. If the `CachedFileData` object is null, the request failed. If an `ILogger` object was provided in configuration, the log will reflect the reason for the failure.

This is an example of a file-retrieval request:

```csharp
string uri = @"https://mcguirev10.com/assets/2024/01-20/bag_dog_corrected.jpg";
int identifier = 123;

// blocks a thread during download
FileCache.RequestFile(uri, identifier, DownloadCallback);

// non-blocking download with synchronous callback
await FileCache.RequestFileAsync(uri, identifier, callback: DownloadCallback);

// non-blocking download with asynchronous callback
await FileCache.RequestFileAsync(uri, identifier, callbackAsync: DownloadCallbackAsync);

// synchronous callback (your application)
public void DownloadCallback(int fileID, CachedFileData fileData) 
{ ... }

// awaited async callback (your application)
public async Task DownloadCallbackAsync(int fileID, CachedFileData fileData)
{ ... }
```

Download operations proceed on a background thread (synchronous or async, and you can await the aysnc operations if you wish). When a download is completed, the downloader will invoke a callback in the client application. If the download operation fails or is canceled, the callback will be invoked with a null `CachedFileData` argument.

Alternately, the program can first call either `GetDataIfCached` or `GetPathnameIfCached` to attempt to retrieve an alread-cached file without starting a download. If the file is not present, these methods will return null.

### Accessing a File

To access the cached file, read the complete pathname from the `GetCachePathname()` method on the `CachedFileData` object returned to your application's callback method.

### Interrupting a Download

If the application no longer needs a file that is still being downloaded, invoke the `ReleaseFile` method.

### Reference Counting

To avoid deleting files which are still in use, a reference-counting feature is provided. When your application no longer needs a cached file, invoke the `ReleaseFile` method. Ref counts are always reset when `Initialize` is called, which means the cache is _not_ suitable for simultaneous use by multiple clients.

While reference-counting can be fragile, again, this library was originally intended for internal use where strict resource management was necessary.

## File Removal

Invoking `DeleteFile` will remove the file associated with the requested URI (if present).

Invoking `ClearCache` will remove all files from the cache.

## Properties

| Property | Description |
|---|---|
|`Configuration`|Stores an `HttpFileCacheConfiguration` object; see instructions at the top of the README.|
|`CacheIndex`|A read-only dictionary keyed on download URIs. It contains `CachedFileData` objects.|
|`CacheSize`|A `long` which represents the approximate space used by the cache in megabytes.|

Because `CacheIndex` is keyed on requested URIs, and because case sensitivity can modify the URI originally requested, the `ParseUri` method is available. It will return the parsed and formatted version of the URI as HttpFileCache would use it, or it returns a null if the URI can't be parsed correctly.

# Command-Line Cache Management

The repository provides `cacheutil` which can inspect and modify a cache directory. A build is available from the repository's Releases page, but it is not included with the NuGet package.

Because the cache is not intended for simultaneous use by multiple programs, do not run this utility while the cache is in use. Simply listing the contents of a cache can change the index.

The program recognizes these commands:

| Command | Description |
|---|---|
| `help` | You're looking at it. |
| `list` | Lists all files in the cache. |
| `about [uri]` | Details of a specific file in the cache. |
| `get [uri]` | Requests a file; loaded from cache if available. |
| `fetch [uri]` | Requests a file; any cached copy removed first. |
| `remove [uri]` | Removes a specific file from the cache. |
| `purge` | Removes all files from the cache. |
| `refresh` | Reloads all files in the cache. |
| `config` | Writes a new `cacheutil.json` with default settings. |

The configuration file `cacheutil.json` is a serialized version of the `FileCacheConfiguration` object. If the file is not present, the default configuration is used. The `config` command will generate this file so that you can change these settings. The config file in the repository often has settings which are only suitable for bug-fixing and testing (for example, very small cache size limits).
