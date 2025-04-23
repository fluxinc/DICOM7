using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog;
using DICOM7.Shared.Cache;

namespace DICOM7.DICOM2ORU
{
  /// <summary>
  /// Manages the cache folder for tracking processed DICOM files
  /// </summary>
  public static class CacheManager
  {
    private const string CACHE_SUFFIX = ".sent";
    private const string CACHE_FOLDER_NAME = "cache";

    static CacheManager() =>
      // Set the cache folder name
      BaseCacheManager.SetConfiguredCacheFolder(CACHE_FOLDER_NAME);

    /// <summary>
    /// Gets the path to the cache folder
    /// </summary>
    public static string CacheFolder => BaseCacheManager.CacheFolder;

    /// <summary>
    /// Sets a custom cache folder path
    /// </summary>
    /// <param name="folderPath">The path to use for caching</param>
    public static void SetConfiguredCacheFolder(string folderPath) =>
      BaseCacheManager.SetConfiguredCacheFolder(folderPath);

    /// <summary>
    /// Ensures the cache folder exists
    /// </summary>
    /// <param name="folder">Optional specific folder, otherwise uses the default</param>
    public static void EnsureCacheFolder(string folder = null) =>
      BaseCacheManager.EnsureCacheFolder(folder);

    /// <summary>
    /// Checks if a file with the given SOP Instance UID has already been processed
    /// </summary>
    /// <param name="sopInstanceUid">The SOP Instance UID to check</param>
    /// <param name="cacheFolder">The cache folder to check in (optional)</param>
    /// <returns>True if the file has been processed, false otherwise</returns>
    public static bool IsAlreadySent(string sopInstanceUid, string cacheFolder = null) =>
      IsItemCached(sopInstanceUid, cacheFolder);

    /// <summary>
    /// Checks if an item with the given ID already exists in the cache
    /// </summary>
    /// <param name="id">The ID to check</param>
    /// <param name="cacheFolder">Optional specific cache folder to check</param>
    /// <returns>True if the item exists, false otherwise</returns>
    public static bool IsItemCached(string id, string cacheFolder = null)
    {
      if (string.IsNullOrEmpty(id))
      {
        return false;
      }

      // Use provided cache folder or default to CacheFolder property
      string folderToUse = cacheFolder ?? CacheFolder;

      // Normalize the path to ensure proper handling of separators
      string normalizedFolder = Path.GetFullPath(folderToUse);

      // Check in the main folder first for legacy files with CACHE_SUFFIX
      string legacyFile = Path.Combine(normalizedFolder, $"{id}{CACHE_SUFFIX}");
      if (File.Exists(legacyFile))
      {
        return true;
      }

      // Also check in the sent folder for newer files
      string sentFolder = Path.Combine(normalizedFolder, BaseCacheManager.SENT_FOLDER_NAME);
      string sentFile = Path.Combine(sentFolder, $"{id}.dcm");
      return File.Exists(sentFile);
    }

    /// <summary>
    /// Marks a DICOM file as processed, either moving it to the sent folder or deleting it based on configuration
    /// </summary>
    /// <param name="sopInstanceUid">The SOP Instance UID that was processed</param>
    /// <param name="keepSentItems">Whether to keep sent items (from config)</param>
    /// <param name="oruMessage">Optional ORU message sent for this DICOM file</param>
    /// <param name="cacheFolder">Optional specific cache folder</param>
    /// <returns>True if successful, false otherwise</returns>
    public static bool MarkAsProcessed(string sopInstanceUid, bool keepSentItems, string oruMessage = null, string cacheFolder = null)
    {
      if (string.IsNullOrEmpty(sopInstanceUid))
      {
        Log.Warning("Cannot mark DICOM as processed: SOP Instance UID is empty");
        return false;
      }

      // Use provided cache folder or default to CacheFolder property
      string folderToUse = cacheFolder ?? CacheFolder;

      // Normalize the path to ensure proper handling of separators
      string normalizedFolder = Path.GetFullPath(folderToUse);

      // Create a temporary file to track this processed DICOM
      string tempFile = Path.Combine(normalizedFolder, $"{sopInstanceUid}.dcm");
      try
      {
        // Write an empty or ORU content file to mark as processed
        string content = oruMessage ?? $"Processed: {DateTime.Now}";
        File.WriteAllText(tempFile, content);

        // Use the shared handler to either move to sent folder or delete
        return BaseCacheManager.HandleProcessedItem(tempFile, keepSentItems, oruMessage);
      }
      catch (Exception e)
      {
        Log.Error(e, "Failed to mark DICOM as processed: {SopInstanceUid}", sopInstanceUid);

        // Clean up the temp file if it exists
        if (File.Exists(tempFile))
        {
          try { File.Delete(tempFile); } catch { /* ignore cleanup errors */ }
        }

        return false;
      }
    }

    /// <summary>
    /// Cleans up the cache folder by removing files older than the specified retention period
    /// </summary>
    /// <param name="cacheFolder">The cache folder to clean</param>
    /// <param name="retentionDays">The number of days to keep files for</param>
    public static void CleanUpCache(string cacheFolder = null, int retentionDays = 3)
    {
      // Use provided folder or default to CacheFolder property
      string folderToUse = cacheFolder ?? CacheFolder;

      // Normalize the path to ensure proper handling of separators
      string normalizedFolder = Path.GetFullPath(folderToUse);

      // Clean up legacy sent files with CACHE_SUFFIX in the main folder
      CleanUpLegacyFiles(normalizedFolder, retentionDays);

      // Clean up the sent folder
      BaseCacheManager.CleanUpSentFolder(normalizedFolder, retentionDays);
    }

    /// <summary>
    /// Cleans up legacy files with CACHE_SUFFIX in the main folder
    /// </summary>
    /// <param name="folderPath">The folder to clean</param>
    /// <param name="days">Number of days to keep files</param>
    private static void CleanUpLegacyFiles(string folderPath, int days)
    {
      if (!Directory.Exists(folderPath))
      {
        return;
      }

      DateTime cutoff = DateTime.Now.AddDays(-days);
      int deleted = 0;

      try
      {
        foreach (string file in Directory.GetFiles(folderPath, $"*{CACHE_SUFFIX}"))
        {
          if (File.GetLastWriteTime(file) < cutoff)
          {
            File.Delete(file);
            deleted++;
          }
        }

        if (deleted > 0)
        {
          Log.Information("Cleaned up {DeletedCount} legacy sent files from '{FolderPath}'", deleted, folderPath);
        }
      }
      catch (Exception e)
      {
        Log.Error(e, "Failed to clean up legacy files: {FolderPath}", folderPath);
      }
    }
  }
}
