using System;
using System.IO;
using System.Runtime.InteropServices;
using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OrderORM
{
    public static class CacheManager
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

                // Otherwise use the default location under the common app folder
                // (which may be using a custom base path if one was specified)
                string cacheFolder = Path.Combine(AppConfig.CommonAppFolder, "cache");
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
                // If the path is relative and we have a custom base path, make it relative to that
                _configuredCacheFolder = Path.GetFullPath(Path.IsPathRooted(cacheFolder) ? 
                    cacheFolder : Path.Combine(AppConfig.CommonAppFolder, cacheFolder));

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
                Log.Information("Creating cache folder \'{NormalizedPath}\'", normalizedPath);
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
            Log.Information("Cleaned up \'{Deleted}\' old ORM files from \'{NormalizedPath}\'", deleted, normalizedPath);
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
            Log.Information("Saved ORM to cache: \'{Filename}\'", filename);
        }
    }
}
