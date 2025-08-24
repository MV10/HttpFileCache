# HttpFileCache (Work in Progress)

This .NET library supports simple HTTP file retrieval and caching. It was created to support image and video downloads for my [Monkey Hi Hat](https://github.com/MV10/monkey-hi-hat) music visualizer, but the functionality seems generally useful so I decided to share the code as a library package instead.

You can configure the storage location (or use the default, which is the user's local temp directory), maximum storage size in megabytes, and the maximum file age in days, after which the given URI will be refreshed when requested. A small command-line utility is included for manual cache inspection and management.

Because this was created for internal use by a project I fully controlled, there are certain safety features I didn't bother to implement. For example, you _could_ change the configuration object once the cache is in use, and you'd probably break things. The rule of thumb is, don't do stupid things, and stupid things won't happen. If you need something but it seems like it might have negative repercussions, just open an Issue and ask me about it.

# Usage

The library is implemented as a simple static class. Cached files are stored with an opaque GUID filename tracked in a simple JSON index. The index is exposed as a read-only dictionary keyed on the file origin URI. Several other data elements are available such as the current size of the cache.

To start, create an `HttpFileCacheConfig` object and assign it to the `HttpFileCache.Configuration` property:

```csharp
// These values represent the default settings.
HttpFileCache.Configuration = new
{
	CacheLocation = null,			// use the default local temp directory
	CacheDirectory = "http_cache",	// directory name inside CacheLocation
	SizeLimit = 512,				// maximum cache size in megabytes
	FileExpirationDays = 30,		// files will be refreshed after this
	CaseSensitivity = false,		// false is safer if you don't need it
	Logger = null,					// a Microsoft.Extensions.Logging logger
	UseExpiredFiles = true,    		// temporary access to expired files
};
```

File retrieval consists of sending the file URI, a unique integer identifier generated and maintaned by your application, and a callback function which receives the integer identifier and a `CachedFileData` object. If the `CachedFileData` object is null, the request failed. If an `ILogger` object was provided in configuration, the log will reflect the reason for the failure.

This is an example of a file-retrieval request:

```csharp
string uri = @"https://mcguirev10.com/assets/2024/01-20/bag_dog_corrected.jpg";
int identifier = 123;

// blocks a thread
HttpFileCache.RequestFile(uri, identifier, DownloadCallback);

// non-blocking
await HttpFileCache.RequestFileAsync(uri, identifier, DownloadCallback);

public void DownloadCallback(int fileID, CachedFileData fileData) 
{ ... }
```

Download operations proceed on a background thread. When a download is completed, the downloader will invoke a callback in the client application. If a file URI is requested and the file is already in the cache, the callback will be invoked immediately. If the file is in the cache but has expired, the callback will (optionally) represent the expired version, which can be used while a replacement is retrieved. When the replacement is available, the callback will be invoked again and the consumer must immediately stop using the expired content, as it will be removed when the callback returns. Of course, if the file is not in the cache, a download is enqueued and the callback is invoked once the file is available.

URI case sensitivity can be tricky. According to the standards, only the protocol (HTTP or HTTPS) and the domain name are case insensitive. In practice, most URIs are fully case insensitive, although non-Windows servers can be picky about this, and some systems intentionally use case sensitivity as part of their identifier schemes (YouTube is a major example). In the application for which HttpFileCache was designed, end-users will specify URIs in config files, and so it is safest to disable case sensitivity by default. On the other hand, if case sensitivity is enabled, the worst that will happen is you may end up with multiple copies of the file in the cache.

