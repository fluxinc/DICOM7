using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DICOM7.Shared.Cache;
using FellowOakDicom;
using Serilog;

namespace DICOM7.ORU2DICOM
{
  /// <summary>
  /// Manages cache folders and persisted artifacts for ORU2DICOM
  /// </summary>
  public static class CacheManager
  {
    private const string INCOMING_FOLDER_NAME = "incoming";
    private const string OUTGOING_FOLDER_NAME = "outgoing";
    private const string ERROR_FOLDER_NAME = "error";

    static CacheManager()
    {
      // Default to the standard cache folder name; ProgramHelpers.InitializeCache may override this later
      BaseCacheManager.SetConfiguredCacheFolder("cache");
    }

    /// <summary>
    /// Root cache folder for the application
    /// </summary>
    public static string CacheFolder => BaseCacheManager.CacheFolder;

    /// <summary>
    /// Ensures cache folder structure exists and applies cleanup rules
    /// </summary>
    public static void Initialize(CacheConfig cacheConfig)
    {
      if (cacheConfig != null && !string.IsNullOrWhiteSpace(cacheConfig.Folder))
      {
        BaseCacheManager.SetConfiguredCacheFolder(cacheConfig.Folder);
      }

      EnsureStructure();

      int retentionDays = cacheConfig != null && cacheConfig.RetentionDays > 0
        ? cacheConfig.RetentionDays
        : 3;

      CleanFolder(Path.Combine(CacheFolder, INCOMING_FOLDER_NAME), retentionDays, "*.hl7");
      CleanFolder(Path.Combine(CacheFolder, OUTGOING_FOLDER_NAME), retentionDays, "*.dcm");
      BaseCacheManager.CleanUpSentFolder(CacheFolder, retentionDays);
    }

    /// <summary>
    /// Ensures the cache folder hierarchy exists
    /// </summary>
    public static void EnsureStructure()
    {
      BaseCacheManager.EnsureCacheFolder();
      EnsureFolder(Path.Combine(CacheFolder, INCOMING_FOLDER_NAME));
      EnsureFolder(Path.Combine(CacheFolder, OUTGOING_FOLDER_NAME));
      EnsureFolder(Path.Combine(CacheFolder, ERROR_FOLDER_NAME));
      BaseCacheManager.EnsureSentFolder(CacheFolder);
    }

    /// <summary>
    /// Returns the path to the incoming HL7 message file for the supplied ID
    /// </summary>
    public static string GetIncomingMessagePath(string messageId)
    {
      return Path.Combine(CacheFolder, INCOMING_FOLDER_NAME, string.Format("{0}.hl7", messageId));
    }

    /// <summary>
    /// Returns the path to the outgoing DICOM artifact for the supplied ID
    /// </summary>
    public static string GetOutgoingDicomPath(string messageId, string suffix = null)
    {
      string fileName = BuildArtifactFileName(messageId, suffix);
      return Path.Combine(CacheFolder, OUTGOING_FOLDER_NAME, fileName);
    }

    /// <summary>
    /// Retrieves the error folder path
    /// </summary>
    public static string ErrorFolder => Path.Combine(CacheFolder, ERROR_FOLDER_NAME);

    /// <summary>
    /// Persists the raw HL7 message into the incoming cache folder
    /// </summary>
    public static string SaveIncomingMessage(CachedORU cachedOru)
    {
      if (cachedOru == null)
      {
        throw new ArgumentNullException(nameof(cachedOru));
      }

      EnsureStructure();

      string path = GetIncomingMessagePath(cachedOru.UUID);
      File.WriteAllText(path, cachedOru.Text);
      return path;
    }

    /// <summary>
    /// Ensures an incoming message exists on disk, recreating it if required
    /// </summary>
    public static void EnsureIncomingMessageExists(CachedORU cachedOru)
    {
      if (cachedOru == null)
      {
        throw new ArgumentNullException(nameof(cachedOru));
      }

      EnsureStructure();

      string path = GetIncomingMessagePath(cachedOru.UUID);
      if (File.Exists(path))
      {
        return;
      }

      File.WriteAllText(path, cachedOru.Text);
    }

    /// <summary>
    /// Saves the generated DICOM payload to the outgoing folder
    /// </summary>
    public static string SaveDicomFile(string messageId, DicomFile dicomFile, string suffix = null)
    {
      if (dicomFile == null)
      {
        throw new ArgumentNullException(nameof(dicomFile));
      }

      EnsureStructure();

      string path = GetOutgoingDicomPath(messageId, suffix);

      // Write to a temporary file first to avoid partial writes on failure
      string tempPath = path + ".tmp";
      dicomFile.Save(tempPath);

      if (File.Exists(path))
      {
        File.Delete(path);
      }

      File.Move(tempPath, path);
      return path;
    }

    /// <summary>
    /// Indicates whether the message has already been processed and archived
    /// </summary>
    public static bool IsAlreadyProcessed(string messageId)
    {
      if (string.IsNullOrWhiteSpace(messageId))
      {
        return false;
      }

      string incomingSentFolder = Path.Combine(CacheFolder, INCOMING_FOLDER_NAME, BaseCacheManager.SENT_FOLDER_NAME);
      string outgoingSentFolder = Path.Combine(CacheFolder, OUTGOING_FOLDER_NAME, BaseCacheManager.SENT_FOLDER_NAME);
      string sanitizedMessageId = SanitizeFilePart(messageId);
      string baseDicomFileName = BuildArtifactFileName(messageId, null);

      string incomingSentHl7 = Path.Combine(incomingSentFolder, string.Format("{0}.hl7", messageId));
      if (File.Exists(incomingSentHl7))
      {
        return true;
      }

      string outgoingSentDicom = Path.Combine(outgoingSentFolder, baseDicomFileName);
      if (File.Exists(outgoingSentDicom))
      {
        return true;
      }

      if (Directory.Exists(outgoingSentFolder))
      {
        string outgoingPattern = string.Format("{0}_*.dcm", sanitizedMessageId);
        if (Directory.GetFiles(outgoingSentFolder, outgoingPattern).Any())
        {
          return true;
        }
      }

      // Fallback to legacy root-level sent folder in case of partially migrated caches
      string legacySentFolder = Path.Combine(CacheFolder, BaseCacheManager.SENT_FOLDER_NAME);
      if (!Directory.Exists(legacySentFolder))
      {
        return false;
      }

      string legacyHl7 = Path.Combine(legacySentFolder, string.Format("{0}.hl7", messageId));
      string legacyDicom = Path.Combine(legacySentFolder, baseDicomFileName);
      if (File.Exists(legacyHl7) || File.Exists(legacyDicom))
      {
        return true;
      }

      string legacyPattern = string.Format("{0}_*.dcm", sanitizedMessageId);
      return Directory.GetFiles(legacySentFolder, legacyPattern).Any();
    }

    /// <summary>
    /// Moves the message and any generated artifacts to the sent folder or deletes them, depending on configuration
    /// </summary>
    public static void MarkAsProcessed(string messageId, bool keepSentItems, string metadata, bool persistDicomFiles, IEnumerable<string> artifactPaths = null)
    {
      string incomingPath = GetIncomingMessagePath(messageId);
      if (File.Exists(incomingPath))
      {
        BaseCacheManager.HandleProcessedItem(incomingPath, keepSentItems, metadata);
      }

      bool keepDicom = persistDicomFiles && keepSentItems;
      IEnumerable<string> paths = artifactPaths ?? new[] { GetOutgoingDicomPath(messageId) };

      foreach (string path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
      {
        if (!File.Exists(path))
        {
          continue;
        }

        BaseCacheManager.HandleProcessedItem(path, keepDicom);
      }
    }

    /// <summary>
    /// Moves the message to the error folder and records the failure reason
    /// </summary>
    public static void MoveMessageToError(string messageId, string messageContent, string reason, IEnumerable<string> artifactPaths = null)
    {
      EnsureStructure();

      string errorPath = Path.Combine(ErrorFolder, string.Format("{0}.hl7", messageId));
      File.WriteAllText(errorPath, messageContent ?? string.Empty);

      if (!string.IsNullOrWhiteSpace(reason))
      {
        File.WriteAllText(errorPath + ".meta", reason);
      }

      DeleteIfExists(GetOutgoingDicomPath(messageId));
      if (artifactPaths != null)
      {
        foreach (string path in artifactPaths)
        {
          DeleteIfExists(path);
        }
      }
      DeleteIfExists(GetIncomingMessagePath(messageId));
    }

    private static void EnsureFolder(string path)
    {
      if (Directory.Exists(path))
      {
        return;
      }

      Directory.CreateDirectory(path);
      Log.Information("Created cache folder: {CacheFolder}", path);
    }

    private static void CleanFolder(string folderPath, int retentionDays, string searchPattern)
    {
      if (!Directory.Exists(folderPath))
      {
        return;
      }

      DateTime cutoff = DateTime.Now.AddDays(-retentionDays);
      foreach (string file in Directory.GetFiles(folderPath, searchPattern))
      {
        try
        {
          if (File.GetLastWriteTime(file) < cutoff)
          {
            File.Delete(file);
            string metaPath = file + ".meta";
            DeleteIfExists(metaPath);
          }
        }
        catch (Exception ex)
        {
          Log.Error(ex, "Failed to clean cache file {FilePath}", file);
        }
      }
    }

    private static void DeleteIfExists(string path)
    {
      if (string.IsNullOrEmpty(path))
      {
        return;
      }

      if (!File.Exists(path))
      {
        return;
      }

      try
      {
        File.Delete(path);
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to delete cache artifact {ArtifactPath}", path);
      }
    }

    private static string BuildArtifactFileName(string messageId, string suffix)
    {
      string safeId = SanitizeFilePart(messageId);
      string safeSuffix = string.IsNullOrWhiteSpace(suffix) ? string.Empty : "_" + SanitizeFilePart(suffix);
      return safeId + safeSuffix + ".dcm";
    }

    private static string SanitizeFilePart(string value)
    {
      if (string.IsNullOrEmpty(value))
      {
        return string.Empty;
      }

      char[] invalid = Path.GetInvalidFileNameChars();
      StringBuilder builder = new StringBuilder(value.Length);
      foreach (char c in value)
      {
        builder.Append(invalid.Contains(c) ? '_' : c);
      }

      return builder.ToString();
    }
  }
}
