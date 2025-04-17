using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog;

namespace DICOM2ORU
{
  public static class CacheManager
  {
    private static string _cacheFolder;
    private const string CACHE_SUFFIX = ".sent";
    private const string CACHE_FOLDER_NAME = "cache";

    /// <summary>
    /// Gets the path to the cache folder
    /// </summary>
    public static string CacheFolder
    {
      get
      {
        if (!string.IsNullOrEmpty(_cacheFolder))
          return _cacheFolder;

        // Default to {AppDataFolder}/cache if not set
        string defaultCacheFolder = Path.Combine(AppConfig.CommonAppFolder, CACHE_FOLDER_NAME);

        try
        {
          if (!Directory.Exists(defaultCacheFolder))
          {
            Directory.CreateDirectory(defaultCacheFolder);
            Log.Information("Created default cache folder: {DefaultCacheFolder}", defaultCacheFolder);
          }

          _cacheFolder = defaultCacheFolder;
        }
        catch (Exception e)
        {
          Log.Error(e, "Failed to create default cache folder: {DefaultCacheFolder}", defaultCacheFolder);
          throw;
        }

        return _cacheFolder;
      }
    }

    /// <summary>
    /// Sets a custom cache folder path
    /// </summary>
    /// <param name="folderPath">The path to use for caching</param>
    public static void SetConfiguredCacheFolder(string folderPath)
    {
      if (string.IsNullOrWhiteSpace(folderPath))
      {
        Log.Warning("Invalid cache folder path specified, using default");
        return;
      }

      // If the path is relative, make it absolute relative to the app folder
      if (!Path.IsPathRooted(folderPath))
      {
        folderPath = Path.Combine(AppConfig.CommonAppFolder, folderPath);
      }

      try
      {
        if (!Directory.Exists(folderPath))
        {
          Directory.CreateDirectory(folderPath);
          Log.Information("Created configured cache folder: {FolderPath}", folderPath);
        }

        _cacheFolder = folderPath;
        Log.Information("Set cache folder to: {CacheFolder}", _cacheFolder);
      }
      catch (Exception e)
      {
        Log.Error(e, "Failed to set custom cache folder: {FolderPath}", folderPath);
        throw;
      }
    }

    /// <summary>
    /// Checks if a file with the given SOP Instance UID has already been processed
    /// </summary>
    /// <param name="sopInstanceUid">The SOP Instance UID to check</param>
    /// <param name="cacheFolder">The cache folder to check in (optional)</param>
    /// <returns>True if the file has been processed, false otherwise</returns>
    public static bool IsAlreadySent(string sopInstanceUid, string cacheFolder = null)
    {
      // Normalize the path to ensure proper handling of separators
      string normalizedFolder = Path.GetFullPath(cacheFolder ?? CacheFolder);
      string cacheFile = Path.Combine(normalizedFolder, $"{sopInstanceUid}{CACHE_SUFFIX}");
      return File.Exists(cacheFile);
    }

    /// <summary>
    /// Saves a processed message to the cache
    /// </summary>
    /// <param name="sopInstanceUid">The SOP Instance UID of the processed DICOM file</param>
    /// <param name="oruMessage">The ORU message that was sent</param>
    /// <param name="cacheFolder">The cache folder to save to (optional)</param>
    public static void SaveToCache(string sopInstanceUid, string oruMessage, string cacheFolder = null)
    {
      // Normalize the path to ensure proper handling of separators
      string normalizedFolder = Path.GetFullPath(cacheFolder ?? CacheFolder);
      string cacheFile = Path.Combine(normalizedFolder, $"{sopInstanceUid}{CACHE_SUFFIX}");

      File.WriteAllText(cacheFile, oruMessage);
      Log.Debug("Saved to cache: {CacheFile}", cacheFile);
    }

    /// <summary>
    /// Cleans up the cache folder by removing files older than the specified retention period
    /// </summary>
    /// <param name="cacheFolder">The cache folder to clean</param>
    /// <param name="retentionDays">The number of days to keep files for</param>
    public static void CleanUpCache(string cacheFolder, int retentionDays)
    {
      DateTime cutoffDate = DateTime.Now.AddDays(-retentionDays);
      Log.Information("Cleaning cache files older than: {CutoffDate}", cutoffDate);

      try
      {
        string normalizedFolder = Path.GetFullPath(cacheFolder ?? CacheFolder);
        string[] cacheFiles = Directory.GetFiles(normalizedFolder, $"*{CACHE_SUFFIX}");
        List<string> oldFiles = cacheFiles.Where(f => File.GetCreationTime(f) < cutoffDate).ToList();

        if (oldFiles.Any())
        {
          Log.Information("Removing {OldFilesCount} cached files older than {RetentionDays} days",
              oldFiles.Count, retentionDays);

          foreach (string file in oldFiles)
          {
            try
            {
              File.Delete(file);
            }
            catch (Exception ex)
            {
              Log.Warning(ex, "Failed to delete cache file: {File}", file);
            }
          }
        }
        else
        {
          Log.Information("No cache files to clean up");
        }
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error cleaning cache folder");
      }
    }
  }
}
