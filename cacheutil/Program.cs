
using HttpFileCache;
using Newtonsoft.Json;

namespace cacheutil;

internal class Program
{
    static async Task Main(string[] args)
    {
        // To generate a new cacheutil.json file:
        //var cfg = new HttpFileCacheConfiguration();
        //var json = JsonConvert.SerializeObject(cfg, Formatting.Indented);
        //File.WriteAllText(@"c:\users\jon\desktop\cacheutil.json", json);
        //Environment.Exit(0);

        Console.WriteLine("cacheutil");

        if (args.Length == 0 || args[0].ToLowerInvariant().Equals("help"))
        {
            ShowHelp();
            return;
        }

        if (File.Exists("cacheutil.json"))
        {
            var json = File.ReadAllText("cacheutil.json");
            FileCache.Configuration = JsonConvert.DeserializeObject<FileCacheConfiguration>(json);
            Console.WriteLine("Loading configuration from cacheutil.json");
        }
        else
        {
            Console.WriteLine("Using default configuration (cacheutil.json not found)");
        }

        // TODO set a console logger

        FileCache.Initialize();

        string uri;
        CachedFileData data;

        switch(args[0].ToLowerInvariant())
        {
            case "list":
                Console.WriteLine($"Cache contains {FileCache.CacheIndex.Count} files.");
                foreach (var kvp in FileCache.CacheIndex)
                {
                    Console.WriteLine($"{kvp.Value.OriginURI} @ {kvp.Value.RetrievalTimestamp}");
                }
                break;

            case "info":
                Console.WriteLine($"Cache contains {FileCache.CacheIndex.Count} files.");
                if (args.Length != 2)
                {
                    ShowHelp();
                    return;
                }
                uri = FileCache.ParseUri(args[1]);
                if (uri is null)
                {
                    Console.WriteLine("The second argument must be a valid URI.");
                    return;
                }
                if(!FileCache.CacheIndex.TryGetValue(uri, out data))
                {
                    Console.WriteLine("The requested URI is not in the cache.");
                    return;
                }
                Console.WriteLine($"Retrieval Timestamp: {data.RetrievalTimestamp}");
                Console.WriteLine($"Size in bytes: {data.Size}");
                Console.WriteLine($"Content Type: {data.ContentType}");
                break;

            case "fetch":
                if(args.Length != 2)
                {
                    ShowHelp();
                    return;
                }
                uri = FileCache.ParseUri(args[1]);
                if (uri is null)
                {
                    Console.WriteLine("The second argument must be a valid URI.");
                    return;
                }
                if (FileCache.CacheIndex.TryGetValue(uri, out data))
                {
                    Console.WriteLine("Removing currently cached version.");
                    FileCache.DeleteFile(uri);
                    return;
                }
                Console.WriteLine("Requesting file.");
                await FileCache.RequestFileAsync(uri, callback: null);
                Console.WriteLine("File request complete.");
                break;

            case "remove":
                if (args.Length != 2)
                {
                    ShowHelp();
                    return;
                }
                uri = FileCache.ParseUri(args[1]);
                if(uri is null)
                {
                    Console.WriteLine("The second argument must be a valid URI.");
                    return;
                }
                if(!FileCache.CacheIndex.ContainsKey(uri))
                {
                    Console.WriteLine("The requested URI is not in the cache.");
                    return;
                }
                FileCache.DeleteFile(uri);
                Console.WriteLine("File removed.");
                break;

            case "purge":
                Console.WriteLine($"Purging {FileCache.CacheIndex.Count} files.");
                FileCache.ClearCache();
                Console.WriteLine($"Cache cleared.");
                break;

            case "refresh":
                Console.WriteLine($"Refreshing {FileCache.CacheIndex.Count} files.");
                Console.WriteLine("... TODO ...");
                break;

            default:
                ShowHelp();
                break;
        }
    }

    static void ShowHelp()
    {
        Console.WriteLine(@"
help          You're looking at it.
list          Lists all files in the cache.
info [uri]    Details of a specific file in the cache.
fetch [uri]   Retrieves a file and stores it the cache.
remove [uri]  Removes a specific file from the cache.
purge         Removes all files from the cache.
refresh       Reloads all files in the cache.
");
    }
}
