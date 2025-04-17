using System;
using System.Collections.Generic;
using System.IO;
using Serilog;

namespace Shared
{
    /// <summary>
    /// Manages retry operations for HL7 messages with a persistent queue
    /// </summary>
    public class RetryManager
    {
        private const string PENDING_SUFFIX = ".pending";
        private const string ATTEMPTS_SUFFIX = ".attempts";

        /// <summary>
        /// Saves a message to the retry queue
        /// </summary>
        /// <param name="messageId">The unique identifier for the message (StudyInstanceUid or SopInstanceUid)</param>
        /// <param name="messageContent">The HL7 message content</param>
        /// <param name="cacheFolder">The folder path where retry files will be stored</param>
        /// <param name="attemptCount">The current attempt count (default is 1)</param>
        public static void SavePendingMessage(string messageId, string messageContent, string cacheFolder,
            int attemptCount = 1)
        {
            // Normalize the path to ensure proper handling of separators
            var normalizedFolder = Path.GetFullPath(cacheFolder);
            var pendingFilename = Path.Combine(normalizedFolder, $"{messageId}{PENDING_SUFFIX}");
            var attemptsFilename = Path.Combine(normalizedFolder, $"{messageId}{ATTEMPTS_SUFFIX}");

            File.WriteAllText(pendingFilename, messageContent);
            File.WriteAllText(attemptsFilename, attemptCount.ToString());

            Log.Information(
                "Saved pending message to retry queue: \'{PendingFilename}\' (attempt {AttemptCount}, will retry indefinitely)",
                pendingFilename, attemptCount);
        }

        /// <summary>
        /// Removes a message from the retry queue
        /// </summary>
        /// <param name="messageId">The unique identifier for the message</param>
        /// <param name="cacheFolder">The folder path where retry files are stored</param>
        public static void RemovePendingMessage(string messageId, string cacheFolder)
        {
            // Normalize the path to ensure proper handling of separators
            var normalizedFolder = Path.GetFullPath(cacheFolder);
            var pendingFilename = Path.Combine(normalizedFolder, $"{messageId}{PENDING_SUFFIX}");
            var attemptsFilename = Path.Combine(normalizedFolder, $"{messageId}{ATTEMPTS_SUFFIX}");

            if (File.Exists(pendingFilename)) File.Delete(pendingFilename);

            if (File.Exists(attemptsFilename)) File.Delete(attemptsFilename);
        }

        /// <summary>
        /// Checks if a message is pending retry
        /// </summary>
        /// <param name="messageId">The unique identifier for the message</param>
        /// <param name="cacheFolder">The folder path where retry files are stored</param>
        /// <returns>True if the message is pending retry, false otherwise</returns>
        public static bool IsPendingRetry(string messageId, string cacheFolder)
        {
            // Normalize the path to ensure proper handling of separators
            var normalizedFolder = Path.GetFullPath(cacheFolder);
            var pendingFilename = Path.Combine(normalizedFolder, $"{messageId}{PENDING_SUFFIX}");
            return File.Exists(pendingFilename);
        }

        /// <summary>
        /// Gets the current attempt count for a message
        /// </summary>
        /// <param name="messageId">The unique identifier for the message</param>
        /// <param name="cacheFolder">The folder path where retry files are stored</param>
        /// <returns>The current attempt count, or 1 if not found</returns>
        public static int GetAttemptCount(string messageId, string cacheFolder)
        {
            // Normalize the path to ensure proper handling of separators
            var normalizedFolder = Path.GetFullPath(cacheFolder);
            var attemptsFilename = Path.Combine(normalizedFolder, $"{messageId}{ATTEMPTS_SUFFIX}");
            if (!File.Exists(attemptsFilename)) return 1; // Default to 1 if file doesn't exist or can't be parsed

            var countStr = File.ReadAllText(attemptsFilename);

            return int.TryParse(countStr, out var count) ? count : 1; // Default to 1 if file doesn't exist or can't be parsed
        }

        /// <summary>
        /// Gets a list of all pending messages ready for retry
        /// </summary>
        /// <typeparam name="T">The type of pending message to return (PendingOrmMessage or PendingOruMessage)</typeparam>
        /// <param name="cacheFolder">The folder path where retry files are stored</param>
        /// <param name="cutoffTime">Only messages older than this time will be returned</param>
        /// <param name="createPendingMessage">A function to create the appropriate pending message object</param>
        /// <returns>A list of pending messages</returns>
        public static IEnumerable<T> GetPendingMessages<T>(
            string cacheFolder,
            DateTime cutoffTime,
            Func<string, string, int, T> createPendingMessage)
        {
            var pendingMessages = new List<T>();

            // Normalize the path to ensure proper handling of separators
            var normalizedFolder = Path.GetFullPath(cacheFolder);

            foreach (var pendingFile in Directory.GetFiles(normalizedFolder, $"*{PENDING_SUFFIX}"))
                try
                {
                    // Only retry messages that have been waiting for at least the retry interval
                    if (File.GetLastWriteTime(pendingFile) < cutoffTime)
                    {
                        var fileName = Path.GetFileName(pendingFile);
                        var messageId = fileName.Substring(0, fileName.Length - PENDING_SUFFIX.Length);
                        var messageContent = File.ReadAllText(pendingFile);
                        var attemptCount = GetAttemptCount(messageId, normalizedFolder);

                        pendingMessages.Add(createPendingMessage(messageId, messageContent, attemptCount));
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Exception processing pending file {PendingFile}: {ExMessage}", pendingFile, ex.Message);
                }

            return pendingMessages;
        }
    }
}
