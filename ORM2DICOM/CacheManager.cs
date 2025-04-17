using System;
using System.IO;
using System.Runtime.InteropServices;
using Serilog;

namespace DICOM7.ORM2DICOM
{
  /// <summary>
  /// Manages the cache folder for ORM message storage
  /// </summary>
  public static class CacheManager
  {
    private static string _cacheFolder;
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
        var defaultCacheFolder = Path.Combine(AppConfig.CommonAppFolder, CACHE_FOLDER_NAME);

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
    /// Ensures the specified cache folder exists
    /// </summary>
    /// <param name="folder">The folder to check/create (defaults to CacheFolder)</param>
    public static void EnsureCacheFolder(string folder = null)
    {
      // Use provided folder or default to CacheFolder property
      string folderToUse = folder ?? CacheFolder;

      // Normalize the path to ensure proper handling of separators
      string normalizedPath = Path.GetFullPath(folderToUse);

      if (!Directory.Exists(normalizedPath))
      {
        Log.Information("Creating cache folder '{NormalizedPath}'", normalizedPath);
        Directory.CreateDirectory(normalizedPath);
      }

      // Ensure the active subfolder exists
      EnsureActiveFolder(normalizedPath);
    }

    /// <summary>
    /// Ensures that the active subfolder exists
    /// </summary>
    /// <param name="basePath">The base cache folder path</param>
    private static void EnsureActiveFolder(string basePath)
    {
      string activePath = Path.Combine(basePath, "active");

      if (!Directory.Exists(activePath))
      {
        Directory.CreateDirectory(activePath);
        Log.Information("Created active ORM folder: {ActivePath}", activePath);
      }
    }

    /// <summary>
    /// Cleans up old files from the cache folder
    /// </summary>
    /// <param name="folder">The folder to clean (defaults to CacheFolder)</param>
    /// <param name="days">Number of days to keep files</param>
    public static void CleanUpCache(string folder = null, int days = 3)
    {
      // Use provided folder or default to CacheFolder property
      string folderToUse = folder ?? CacheFolder;

      // Normalize the path to ensure proper handling of separators
      string normalizedPath = Path.GetFullPath(folderToUse);

      // Clean the active subfolder
      CleanFolder(Path.Combine(normalizedPath, "active"), days);
    }

    /// <summary>
    /// Cleans up files older than the specified number of days in a folder
    /// </summary>
    /// <param name="folderPath">The folder to clean</param>
    /// <param name="days">Number of days to keep files</param>
    private static void CleanFolder(string folderPath, int days)
    {
      if (!Directory.Exists(folderPath))
      {
        return;
      }

      DateTime cutoff = DateTime.Now.AddDays(-days);
      int deleted = 0;

      foreach (string file in Directory.GetFiles(folderPath, "*.hl7"))
      {
        if (File.GetLastWriteTime(file) >= cutoff) continue;

        try
        {
          File.Delete(file);
          deleted++;
        }
        catch (Exception e)
        {
          Log.Error(e, "Failed to delete expired file: {FilePath}", file);
        }
      }

      if (deleted > 0)
      {
        Log.Information("Cleaned up {DeletedCount} old files from '{FolderPath}'", deleted, folderPath);
      }
    }

    /// <summary>
    /// Checks if an ORM message with the given ID already exists in the cache
    /// </summary>
    /// <param name="messageId">The message ID to check</param>
    /// <param name="cacheFolder">Optional specific cache folder to check</param>
    /// <returns>True if the message exists, false otherwise</returns>
    public static bool MessageExists(string messageId, string cacheFolder = null)
    {
      if (string.IsNullOrEmpty(messageId))
      {
        return false;
      }

      // Use provided cache folder or default to CacheFolder property
      string folderToUse = cacheFolder ?? CacheFolder;

      // Normalize the path to ensure proper handling of separators
      string normalizedFolder = Path.GetFullPath(folderToUse);

      // Check in the active folder
      string messagePath = Path.Combine(normalizedFolder, "active", $"{messageId}.hl7");
      return File.Exists(messagePath);
    }

    /// <summary>
    /// Saves an ORM message to the cache
    /// </summary>
    /// <param name="messageId">The message ID to use as the filename</param>
    /// <param name="ormMessage">The ORM message content</param>
    /// <param name="cacheFolder">Optional specific cache folder to use</param>
    public static void SaveToCache(string messageId, string ormMessage, string cacheFolder = null)
    {
      if (string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(ormMessage))
      {
        Log.Warning("Cannot save ORM message: ID or message content is empty");
        return;
      }

      // Use provided cache folder or default to CacheFolder property
      string folderToUse = cacheFolder ?? CacheFolder;

      // Normalize the path to ensure proper handling of separators
      string normalizedFolder = Path.GetFullPath(folderToUse);

      // Save to the active folder
      string activeFolder = Path.Combine(normalizedFolder, "active");
      if (!Directory.Exists(activeFolder))
      {
        Directory.CreateDirectory(activeFolder);
      }

      string filePath = Path.Combine(activeFolder, $"{messageId}.hl7");

      try
      {
        // Write to a temporary file first to avoid partial writes
        string tempPath = filePath + ".tmp";
        File.WriteAllText(tempPath, ormMessage);

        // Delete the destination if it exists, then move the temp file
        if (File.Exists(filePath))
        {
          File.Delete(filePath);
        }

        File.Move(tempPath, filePath);
        Log.Information("Saved ORM message to cache: '{FilePath}'", filePath);
      }
      catch (Exception e)
      {
        Log.Error(e, "Failed to save ORM message to cache: '{FilePath}'", filePath);
      }
    }
  }
}
