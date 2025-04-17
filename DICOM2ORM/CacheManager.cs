using System;
using System.IO;
using System.Runtime.InteropServices;
using Serilog;
using DICOM7.Shared.Cache;

namespace DICOM7.DICOM2ORM
{
  /// <summary>
  /// Manages the cache folder for tracking sent ORM messages
  /// </summary>
  public static class CacheManager
  {
    private const string CACHE_FOLDER_NAME = "cache";
    private const string CACHE_EXTENSION = ".hl7";

    static CacheManager()
    {
      // Set the cache folder name
      BaseCacheManager.SetConfiguredCacheFolder(CACHE_FOLDER_NAME);
    }

    /// <summary>
    /// Gets the path to the cache folder
    /// </summary>
    public static string CacheFolder => BaseCacheManager.CacheFolder;

    /// <summary>
    /// Sets a custom cache folder path
    /// </summary>
    /// <param name="cacheFolder">The custom folder path to use</param>
    public static void SetConfiguredCacheFolder(string cacheFolder)
    {
      BaseCacheManager.SetConfiguredCacheFolder(cacheFolder);
    }

    /// <summary>
    /// Ensures the cache folder exists
    /// </summary>
    /// <param name="folder">The folder to check/create (defaults to CacheFolder)</param>
    public static void EnsureCacheFolder(string folder = null)
    {
      BaseCacheManager.EnsureCacheFolder(folder);
    }

    /// <summary>
    /// Cleans up old files from the cache folder
    /// </summary>
    /// <param name="folder">The folder to clean (defaults to CacheFolder)</param>
    /// <param name="days">Number of days to keep files</param>
    public static void CleanUpCache(string folder = null, int days = 30)
    {
      // Use provided folder or default to CacheFolder property
      string folderToUse = folder ?? CacheFolder;

      // Normalize the path to ensure proper handling of separators
      string normalizedPath = Path.GetFullPath(folderToUse);

      // Clean up pending files in the main folder
      CleanUpPendingFiles(normalizedPath, days);

      // Clean up the sent folder
      BaseCacheManager.CleanUpSentFolder(normalizedPath, days);
    }

    /// <summary>
    /// Cleans up pending ORM files in the main folder
    /// </summary>
    /// <param name="folderPath">The folder to clean</param>
    /// <param name="days">Number of days to keep files</param>
    private static void CleanUpPendingFiles(string folderPath, int days)
    {
      if (!Directory.Exists(folderPath))
      {
        return;
      }

      try
      {
        DateTime cutoff = DateTime.Now.AddDays(-days);
        int deleted = 0;
        foreach (string file in Directory.GetFiles(folderPath, $"*{CACHE_EXTENSION}"))
        {
          if (File.GetLastWriteTime(file) < cutoff)
          {
            File.Delete(file);
            deleted++;
          }
        }

        if (deleted > 0)
        {
          Log.Information("Cleaned up {Deleted} old pending ORM files from '{NormalizedPath}'", deleted, folderPath);
        }
      }
      catch (Exception e)
      {
        Log.Error(e, "Failed to clean up pending files: {FolderPath}", folderPath);
      }
    }

    /// <summary>
    /// Checks if a study has already been sent to the HL7 receiver
    /// </summary>
    /// <param name="studyInstanceUid">The Study Instance UID to check</param>
    /// <param name="cacheFolder">Optional specific cache folder to check</param>
    /// <returns>True if the study has been sent, false otherwise</returns>
    public static bool IsAlreadySent(string studyInstanceUid, string cacheFolder = null)
    {
      if (string.IsNullOrEmpty(studyInstanceUid))
      {
        return false;
      }

      // Use provided cache folder or default to CacheFolder property
      string folderToUse = cacheFolder ?? CacheFolder;

      // Normalize the path to ensure proper handling of separators
      string normalizedFolder = Path.GetFullPath(folderToUse);

      // Check in the main folder first (pending files)
      string pendingFile = Path.Combine(normalizedFolder, $"{studyInstanceUid}{CACHE_EXTENSION}");
      if (File.Exists(pendingFile))
      {
        return true;
      }

      // Also check in the sent folder
      string sentFolder = Path.Combine(normalizedFolder, BaseCacheManager.SENT_FOLDER_NAME);
      string sentFile = Path.Combine(sentFolder, $"{studyInstanceUid}{CACHE_EXTENSION}");
      return File.Exists(sentFile);
    }

    /// <summary>
    /// Saves an ORM message to the cache
    /// </summary>
    /// <param name="studyInstanceUid">The Study Instance UID to use as the filename</param>
    /// <param name="ormMessage">The ORM message content</param>
    /// <param name="cacheFolder">Optional specific cache folder to use</param>
    public static void SaveToCache(string studyInstanceUid, string ormMessage, string cacheFolder = null)
    {
      if (string.IsNullOrEmpty(studyInstanceUid) || string.IsNullOrEmpty(ormMessage))
      {
        Log.Warning("Cannot save ORM message: ID or message content is empty");
        return;
      }

      // Use provided cache folder or default to CacheFolder property
      string folderToUse = cacheFolder ?? CacheFolder;

      // Normalize the path to ensure proper handling of separators
      string normalizedFolder = Path.GetFullPath(folderToUse);
      string filename = Path.Combine(normalizedFolder, $"{studyInstanceUid}{CACHE_EXTENSION}");

      try
      {
        // Write to a temporary file first to avoid partial writes
        string tempPath = filename + ".tmp";
        File.WriteAllText(tempPath, ormMessage);

        // Delete the destination if it exists, then move the temp file
        if (File.Exists(filename))
        {
          File.Delete(filename);
        }

        File.Move(tempPath, filename);
        Log.Information("Saved ORM to cache: '{Filename}'", filename);
      }
      catch (Exception e)
      {
        Log.Error(e, "Failed to save ORM message to cache: '{Filename}'", filename);
      }
    }

    /// <summary>
    /// Marks an ORM message as processed, either moving it to the sent folder or deleting it based on configuration
    /// </summary>
    /// <param name="studyInstanceUid">The Study Instance UID that was processed</param>
    /// <param name="keepSentItems">Whether to keep sent items (from config)</param>
    /// <param name="metadata">Optional metadata about the processing</param>
    /// <param name="cacheFolder">Optional specific cache folder</param>
    /// <returns>True if successful, false otherwise</returns>
    public static bool MarkAsProcessed(string studyInstanceUid, bool keepSentItems, string metadata = null, string cacheFolder = null)
    {
      if (string.IsNullOrEmpty(studyInstanceUid))
      {
        Log.Warning("Cannot mark ORM as processed: Study Instance UID is empty");
        return false;
      }

      // Use provided cache folder or default to CacheFolder property
      string folderToUse = cacheFolder ?? CacheFolder;

      // Normalize the path to ensure proper handling of separators
      string normalizedFolder = Path.GetFullPath(folderToUse);
      string filePath = Path.Combine(normalizedFolder, $"{studyInstanceUid}{CACHE_EXTENSION}");

      if (!File.Exists(filePath))
      {
        Log.Warning("Cannot mark ORM as processed: File does not exist: {FilePath}", filePath);
        return false;
      }

      // Use the shared handler to either move to sent folder or delete
      return BaseCacheManager.HandleProcessedItem(filePath, keepSentItems, metadata);
    }
  }
}
