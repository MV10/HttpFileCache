
using HttpFileCache;
using Microsoft.VisualBasic.FileIO;
using Newtonsoft.Json;

namespace cacheutil;

internal class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("cacheutil\n");

        if (args.Length == 0 || args[0].ToLowerInvariant().Equals("help"))
        {
            ShowHelp();
            return;
        }

        if (File.Exists("cacheutil.json"))
        {
            var json = File.ReadAllText("cacheutil.json");
            FileCache.Configuration = JsonConvert.DeserializeObject<FileCacheConfiguration>(json);
            Console.WriteLine("Loading configuration from cacheutil.json.");
        }
        else
        {
            Console.WriteLine("Using default configuration (cacheutil.json not found).");
        }
        Console.WriteLine("Remember: The utility's configuration may not match the cache owner's config.\n");

        // TODO set a console logger

        FileCache.Initialize();
        Console.WriteLine($"Cache directory:\n{FileCache.Configuration.CacheFullPath}\n");

        string uri;
        CachedFileData data;
        double megabytes = FileCache.CacheSize / 1024;
        double percent = 100d * (FileCache.CacheSize / (FileCache.Configuration.SizeLimit * 1024d));

        switch(args[0].ToLowerInvariant())
        {
            case "list":
                Console.WriteLine($"Cache contains {FileCache.CacheIndex.Count} files occupying {FileCache.CacheSize} bytes ({megabytes:F2}MB is {percent:F2}% of alloted space).");
                foreach (var kvp in FileCache.CacheIndex)
                {
                    Console.WriteLine($"{kvp.Value.OriginURI} @ {kvp.Value.RetrievalTimestamp}");
                }
                break;

            case "info":
                Console.WriteLine($"Cache contains {FileCache.CacheIndex.Count} files occupying {FileCache.CacheSize} bytes ({percent:F2}% used of {megabytes:F2}MB).");
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
                double age = (double)DateTime.Now.Subtract(data.RetrievalTimestamp).Minutes / (24d * 60d);
                Console.WriteLine($"  Retrieval timestamp: {data.RetrievalTimestamp}");
                Console.WriteLine($"  Age in days:         {age:F3}");
                Console.WriteLine($"  Size:                {data.Size} bytes");
                Console.WriteLine($"  ContentType:         {data.ContentType}");
                break;

            case "get":
            case "fetch":
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
                if (args[0].ToLowerInvariant().Equals("fetch") && FileCache.CacheIndex.TryGetValue(uri, out data))
                {
                    Console.WriteLine("Removing currently cached version.");
                    FileCache.DeleteFile(uri);
                }
                Console.WriteLine("Requesting file with await and synchronous callback.");
                // Here, using await ensures the callback is invoked before the program exits.
                await FileCache.RequestFileAsync(uri, callback: FetchCallback);
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

            case "config":
                if(File.Exists("cacheutil.json"))
                {
                    Console.Write("Overwrite existing config file?\n(Y)> ");
                    var key = Console.ReadKey(false);
                    Console.WriteLine();
                    if (key.Key != ConsoleKey.Y) break;
                    File.Delete("cacheutil.json");
                }
                var json = JsonConvert.SerializeObject(new FileCacheConfiguration(), Formatting.Indented);
                File.WriteAllText("cacheutil.json", json);
                Console.WriteLine("New default configuration written to cacheutil.json.");
                break;

            default:
                ShowHelp();
                break;
        }
        Console.WriteLine();
    }

    public static void FetchCallback(int id, CachedFileData data)
    {
        if(data is null)
        {
            Console.WriteLine("File unavailable (callback response was null).");
            return;
        }

        double age = (double)DateTime.Now.Subtract(data.RetrievalTimestamp).Minutes / (24d * 60d);

        Console.WriteLine("File available:");
        Console.WriteLine($"  Current time:        {DateTime.Now}");
        Console.WriteLine($"  Retrieval timestamp: {data.RetrievalTimestamp}");
        Console.WriteLine($"  Age in days:         {age:F3}");
        Console.WriteLine($"  Size:                {data.Size} bytes");
        Console.WriteLine($"  ContentType:         {data.ContentType}");
    }

    static void ShowHelp()
    {
        Console.WriteLine(@"
help          You're looking at it.
list          Lists all files in the cache.
about [uri]   Details of a specific file in the cache.
get [uri]     Requests a file; loaded from cache if available.
fetch [uri]   Requests a file; any cached copy removed first.
remove [uri]  Removes a specific file from the cache.
purge         Removes all files from the cache.
refresh       Reloads all files in the cache.
config        Writes a new cacheutil.json with default settings.

WARNING: SIMPLY READING THE CONTENTS OF A CACHE CAN CHANGE THE INDEX.
DO NOT USE THIS UTILITY ON A CACHE ACTIVELY IN USE BY ANOTHER PROGRAM.
");
    }
}
