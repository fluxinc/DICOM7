using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OrderORM
{
    public class RetryManager
    {
        private const string PENDING_SUFFIX = ".pending";
        private const string ATTEMPTS_SUFFIX = ".attempts";

        public static void SavePendingMessage(string studyInstanceUid, string ormMessage, string cacheFolder, int attemptCount = 1)
        {
            // Normalize the path to ensure proper handling of separators
            string normalizedFolder = Path.GetFullPath(cacheFolder);
            string pendingFilename = Path.Combine(normalizedFolder, $"{studyInstanceUid}{PENDING_SUFFIX}");
            string attemptsFilename = Path.Combine(normalizedFolder, $"{studyInstanceUid}{ATTEMPTS_SUFFIX}");

            File.WriteAllText(pendingFilename, ormMessage);
            File.WriteAllText(attemptsFilename, attemptCount.ToString());

            Console.WriteLine($"{DateTime.Now} - Saved pending ORM to retry queue: '{pendingFilename}' (attempt {attemptCount}, will retry indefinitely)");
        }

        public static void RemovePendingMessage(string studyInstanceUid, string cacheFolder)
        {
            // Normalize the path to ensure proper handling of separators
            string normalizedFolder = Path.GetFullPath(cacheFolder);
            string pendingFilename = Path.Combine(normalizedFolder, $"{studyInstanceUid}{PENDING_SUFFIX}");
            string attemptsFilename = Path.Combine(normalizedFolder, $"{studyInstanceUid}{ATTEMPTS_SUFFIX}");

            if (File.Exists(pendingFilename))
            {
                File.Delete(pendingFilename);
            }

            if (File.Exists(attemptsFilename))
            {
                File.Delete(attemptsFilename);
            }
        }

        public static bool IsPendingRetry(string studyInstanceUid, string cacheFolder)
        {
            // Normalize the path to ensure proper handling of separators
            string normalizedFolder = Path.GetFullPath(cacheFolder);
            string pendingFilename = Path.Combine(normalizedFolder, $"{studyInstanceUid}{PENDING_SUFFIX}");
            return File.Exists(pendingFilename);
        }

        public static int GetAttemptCount(string studyInstanceUid, string cacheFolder)
        {
            // Normalize the path to ensure proper handling of separators
            string normalizedFolder = Path.GetFullPath(cacheFolder);
            string attemptsFilename = Path.Combine(normalizedFolder, $"{studyInstanceUid}{ATTEMPTS_SUFFIX}");
            if (File.Exists(attemptsFilename))
            {
                string countStr = File.ReadAllText(attemptsFilename);
                if (int.TryParse(countStr, out int count))
                {
                    return count;
                }
            }
            return 1; // Default to 1 if file doesn't exist or can't be parsed
        }

        public static IEnumerable<PendingOrmMessage> GetPendingMessages(string cacheFolder, DateTime cutoffTime)
        {
            var pendingMessages = new List<PendingOrmMessage>();

            // Normalize the path to ensure proper handling of separators
            string normalizedFolder = Path.GetFullPath(cacheFolder);

            foreach (var pendingFile in Directory.GetFiles(normalizedFolder, $"*{PENDING_SUFFIX}"))
            {
                try
                {
                    // Only retry messages that have been waiting for at least the retry interval
                    if (File.GetLastWriteTime(pendingFile) < cutoffTime)
                    {
                        string fileName = Path.GetFileName(pendingFile);
                        string studyInstanceUid = fileName.Substring(0, fileName.Length - PENDING_SUFFIX.Length);
                        string ormMessage = File.ReadAllText(pendingFile);
                        int attemptCount = GetAttemptCount(studyInstanceUid, normalizedFolder);

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
                    Console.WriteLine($"{DateTime.Now} - ERROR processing pending file {pendingFile}: {ex.Message}");
                }
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
}
