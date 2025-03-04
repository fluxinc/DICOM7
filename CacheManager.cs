using System;
using System.IO;

namespace OrderORM
{


    public class CacheManager
    {
        public static void EnsureCacheFolder(string folder)
        {
            if (!Directory.Exists(folder))
            {
                Console.WriteLine($"{DateTime.Now} - Creating cache folder '{folder}'");
                Directory.CreateDirectory(folder);
            }
        }

        public static void CleanUpCache(string folder, int days)
        {
            DateTime cutoff = DateTime.Now.AddDays(-days);
            int deleted = 0;
            foreach (var file in Directory.GetFiles(folder, "*.orm"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            Console.WriteLine($"{DateTime.Now} - Cleaned up {deleted} old ORM files from '{folder}'");
        }

        public static bool IsAlreadySent(string studyInstanceUid, string cacheFolder)
        {
            string filename = Path.Combine(cacheFolder, $"{studyInstanceUid}.orm");
            return File.Exists(filename);
        }

        public static void SaveToCache(string studyInstanceUid, string ormMessage, string cacheFolder)
        {
            string filename = Path.Combine(cacheFolder, $"{studyInstanceUid}.orm");
            File.WriteAllText(filename, ormMessage);
            Console.WriteLine($"{DateTime.Now} - Saved ORM to cache: '{filename}'");
        }
    }
}