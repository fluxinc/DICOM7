using System;
using System.IO;
using System.Runtime.InteropServices;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OrderORM
{
    public class CacheManager
    {
        private static string _configuredCacheFolder;

        public static string CacheFolder
        {
            get
            {
                // If a cache folder is explicitly configured, use that
                if (!string.IsNullOrWhiteSpace(_configuredCacheFolder))
                {
                    return _configuredCacheFolder;
                }

                // Otherwise use the default location
                var cacheFolder = Path.Combine(AppConfig.CommonAppFolder, "cache");
                if (!Directory.Exists(cacheFolder))
                {
                    Directory.CreateDirectory(cacheFolder);
                }
                return cacheFolder;
            }
        }

        public static void SetConfiguredCacheFolder(string cacheFolder)
        {
            if (!string.IsNullOrWhiteSpace(cacheFolder))
            {
                _configuredCacheFolder = Path.GetFullPath(cacheFolder);

                // Ensure the configured folder exists
                if (!Directory.Exists(_configuredCacheFolder))
                {
                    Directory.CreateDirectory(_configuredCacheFolder);
                }
            }
            else
            {
                _configuredCacheFolder = null;
            }
        }

        public static void EnsureCacheFolder(string folder = null)
        {
            // Use provided folder or default to CacheFolder property
            string folderToUse = folder ?? CacheFolder;

            // Normalize the path to ensure proper handling of separators
            string normalizedPath = Path.GetFullPath(folderToUse);

            if (!Directory.Exists(normalizedPath))
            {
                Console.WriteLine($"{DateTime.Now} - Creating cache folder '{normalizedPath}'");
                Directory.CreateDirectory(normalizedPath);
            }
        }

        public static void CleanUpCache(string folder = null, int days = 30)
        {
            // Use provided folder or default to CacheFolder property
            string folderToUse = folder ?? CacheFolder;

            // Normalize the path to ensure proper handling of separators
            string normalizedPath = Path.GetFullPath(folderToUse);

            DateTime cutoff = DateTime.Now.AddDays(-days);
            int deleted = 0;
            foreach (var file in Directory.GetFiles(normalizedPath, "*.hl7"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            Console.WriteLine($"{DateTime.Now} - Cleaned up {deleted} old ORM files from '{normalizedPath}'");
        }

        public static bool IsAlreadySent(string studyInstanceUid, string cacheFolder = null)
        {
            // Use provided cache folder or default to CacheFolder property
            string folderToUse = cacheFolder ?? CacheFolder;

            // Normalize the path to ensure proper handling of separators
            string normalizedFolder = Path.GetFullPath(folderToUse);
            string filename = Path.Combine(normalizedFolder, $"{studyInstanceUid}.hl7");
            return File.Exists(filename);
        }

        public static void SaveToCache(string studyInstanceUid, string ormMessage, string cacheFolder = null)
        {
            // Use provided cache folder or default to CacheFolder property
            string folderToUse = cacheFolder ?? CacheFolder;

            // Normalize the path to ensure proper handling of separators
            string normalizedFolder = Path.GetFullPath(folderToUse);
            string filename = Path.Combine(normalizedFolder, $"{studyInstanceUid}.hl7");
            File.WriteAllText(filename, ormMessage);
            Console.WriteLine($"{DateTime.Now} - Saved ORM to cache: '{filename}'");
        }
    }
}
