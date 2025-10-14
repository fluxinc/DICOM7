using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;
using Serilog;
using DICOM7.Shared;

namespace DICOM7.DICOM2ORU
{
    public class DicomImageProcessor
    {
        private readonly Config _config;
        private readonly string _oruTemplate;
        private readonly List<ProcessingResult> _processingResults;

        public DicomImageProcessor(Config config, string oruTemplate)
        {
            _config = config;
            _oruTemplate = oruTemplate;
            _processingResults = new List<ProcessingResult>();
        }

        /// <summary>
        /// Processes any pending retry messages in the cache folder
        /// </summary>
        public void ProcessPendingMessages()
        {
            try
            {
                // Initialize the cache folder
                CacheManager.EnsureCacheFolder();

                // Create outgoing folder if it doesn't exist
                string outgoingFolder = Path.Combine(CacheManager.CacheFolder, "outgoing");
                if (!Directory.Exists(outgoingFolder))
                {
                    Directory.CreateDirectory(outgoingFolder);
                    Log.Information("Created outgoing ORU folder: {OutgoingPath}", outgoingFolder);
                }

                // Get pending messages based on retry interval
                DateTime cutoffTime = DateTime.Now.AddMinutes(-_config.Retry.RetryIntervalMinutes);
                IEnumerable<PendingOruMessage> pendingMessages = RetryManager.GetPendingMessages<PendingOruMessage>(
                    CacheManager.CacheFolder,
                    cutoffTime,
                    (id, content, attemptCount) => new PendingOruMessage { SopInstanceUid = id, OruMessage = content, AttemptCount = attemptCount }
                );

                IEnumerable<PendingOruMessage> pendingOruMessages = pendingMessages.ToList();
                if (!pendingOruMessages.Any())
                {
                    Log.Debug("No pending messages found for retry");
                    return;
                }

                Log.Information("Found {Count} pending ORU messages to retry", pendingOruMessages.Count());

                foreach (PendingOruMessage pendingMessage in pendingOruMessages)
                {
                    try
                    {
                        // Save to outgoing folder for pickup by sender
                        string oruFilePath = Path.Combine(outgoingFolder, $"{pendingMessage.SopInstanceUid}.oru");
                        File.WriteAllText(oruFilePath, pendingMessage.OruMessage);

                        // Remove from retry queue if we could save it to outgoing
                        RetryManager.RemovePendingMessage(pendingMessage.SopInstanceUid, CacheManager.CacheFolder);

                        Log.Information("Moved retry message to outgoing folder: {SopInstanceUid} (attempt {AttemptCount})",
                            pendingMessage.SopInstanceUid, pendingMessage.AttemptCount);
                    }
                    catch (Exception ex)
                    {
                        // Increment attempt count and keep in retry queue
                        RetryManager.SavePendingMessage(
                            pendingMessage.SopInstanceUid,
                            pendingMessage.OruMessage,
                            CacheManager.CacheFolder,
                            pendingMessage.AttemptCount + 1
                        );

                        Log.Error(ex, "Failed to process retry message {SopInstanceUid} (attempt {AttemptCount}): {Message}",
                            pendingMessage.SopInstanceUid, pendingMessage.AttemptCount, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing pending messages: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Returns any processing results from operations
        /// </summary>
        public IEnumerable<ProcessingResult> GetProcessingResults() => _processingResults.AsEnumerable();

        /// <summary>
        /// Helper class for pending ORU messages in the retry queue
        /// </summary>
        private class PendingOruMessage
        {
            public string SopInstanceUid { get; set; }
            public string OruMessage { get; set; }
            public int AttemptCount { get; set; }
        }
    }

    /// <summary>
    /// Holds the result of a DICOM processing operation
    /// </summary>
    public class ProcessingResult
    {
        public string SopInstanceUid { get; set; }
        public string FileName { get; set; }
        public bool Success { get; set; }
        public bool HasPdf { get; set; }
    }
}
