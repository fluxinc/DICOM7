using System;
using System.Collections.Generic;
using System.IO;
using Serilog;

namespace OrderORM;

public class RetryManager
{
    private const string PENDING_SUFFIX = ".pending";
    private const string ATTEMPTS_SUFFIX = ".attempts";

    public static void SavePendingMessage(string studyInstanceUid, string ormMessage, string cacheFolder,
        int attemptCount = 1)
    {
        // Normalize the path to ensure proper handling of separators
        var normalizedFolder = Path.GetFullPath(cacheFolder);
        var pendingFilename = Path.Combine(normalizedFolder, $"{studyInstanceUid}{PENDING_SUFFIX}");
        var attemptsFilename = Path.Combine(normalizedFolder, $"{studyInstanceUid}{ATTEMPTS_SUFFIX}");

        File.WriteAllText(pendingFilename, ormMessage);
        File.WriteAllText(attemptsFilename, attemptCount.ToString());

        Log.Information(
            "Saved pending ORM to retry queue: \'{PendingFilename}\' (attempt {AttemptCount}, will retry indefinitely)", 
            pendingFilename, attemptCount);
    }

    public static void RemovePendingMessage(string studyInstanceUid, string cacheFolder)
    {
        // Normalize the path to ensure proper handling of separators
        var normalizedFolder = Path.GetFullPath(cacheFolder);
        var pendingFilename = Path.Combine(normalizedFolder, $"{studyInstanceUid}{PENDING_SUFFIX}");
        var attemptsFilename = Path.Combine(normalizedFolder, $"{studyInstanceUid}{ATTEMPTS_SUFFIX}");

        if (File.Exists(pendingFilename)) File.Delete(pendingFilename);

        if (File.Exists(attemptsFilename)) File.Delete(attemptsFilename);
    }

    public static bool IsPendingRetry(string studyInstanceUid, string cacheFolder)
    {
        // Normalize the path to ensure proper handling of separators
        var normalizedFolder = Path.GetFullPath(cacheFolder);
        var pendingFilename = Path.Combine(normalizedFolder, $"{studyInstanceUid}{PENDING_SUFFIX}");
        return File.Exists(pendingFilename);
    }

    public static int GetAttemptCount(string studyInstanceUid, string cacheFolder)
    {
        // Normalize the path to ensure proper handling of separators
        var normalizedFolder = Path.GetFullPath(cacheFolder);
        var attemptsFilename = Path.Combine(normalizedFolder, $"{studyInstanceUid}{ATTEMPTS_SUFFIX}");
        if (!File.Exists(attemptsFilename)) return 1; // Default to 1 if file doesn't exist or can't be parsed
        
        var countStr = File.ReadAllText(attemptsFilename);
        
        return int.TryParse(countStr, out var count) ? count : 1; // Default to 1 if file doesn't exist or can't be parsed
    }

    public static IEnumerable<PendingOrmMessage> GetPendingMessages(string cacheFolder, DateTime cutoffTime)
    {
        var pendingMessages = new List<PendingOrmMessage>();

        // Normalize the path to ensure proper handling of separators
        var normalizedFolder = Path.GetFullPath(cacheFolder);

        foreach (var pendingFile in Directory.GetFiles(normalizedFolder, $"*{PENDING_SUFFIX}"))
            try
            {
                // Only retry messages that have been waiting for at least the retry interval
                if (File.GetLastWriteTime(pendingFile) < cutoffTime)
                {
                    var fileName = Path.GetFileName(pendingFile);
                    var studyInstanceUid = fileName.Substring(0, fileName.Length - PENDING_SUFFIX.Length);
                    var ormMessage = File.ReadAllText(pendingFile);
                    var attemptCount = GetAttemptCount(studyInstanceUid, normalizedFolder);

                    pendingMessages.Add(new PendingOrmMessage
                    {
                        StudyInstanceUid = studyInstanceUid,
                        OrmMessage = ormMessage,
                        AttemptCount = attemptCount
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error("Exception processing pending file {PendingFile}: {ExMessage}", pendingFile, ex.Message);
            }

        return pendingMessages;
    }
}

public class PendingOrmMessage
{
    public string StudyInstanceUid { get; set; }
    public string OrmMessage { get; set; }
    public int AttemptCount { get; set; }
}