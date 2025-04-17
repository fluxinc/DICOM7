using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;
using Serilog;
using Shared;

namespace DICOM2ORU
{
    public class DicomImageProcessor
    {
        private readonly Config _config;
        private readonly string _oruTemplate;
        private List<ProcessingResult> _processingResults;
        private string _inputFolderPath;
        private string _archiveFolderPath;
        private string _errorFolderPath;

        public DicomImageProcessor(Config config, string oruTemplate)
        {
            _config = config;
            _oruTemplate = oruTemplate;
            _processingResults = new List<ProcessingResult>();

            // Initialize folder paths
            InitializeFolders();
        }

        private void InitializeFolders()
        {
            try
            {
                // Get input folder path (can be absolute or relative to app folder)
                if (Path.IsPathRooted(_config.Input.InputFolder))
                {
                    _inputFolderPath = _config.Input.InputFolder;
                }
                else
                {
                    _inputFolderPath = Path.Combine(AppConfig.CommonAppFolder, _config.Input.InputFolder);
                }

                // Create input folder if it doesn't exist
                if (!Directory.Exists(_inputFolderPath))
                {
                    Directory.CreateDirectory(_inputFolderPath);
                    Log.Information("Created input folder: {InputFolder}", _inputFolderPath);
                }

                // Get archive folder path
                if (Path.IsPathRooted(_config.Input.ArchiveFolder))
                {
                    _archiveFolderPath = _config.Input.ArchiveFolder;
                }
                else
                {
                    _archiveFolderPath = Path.Combine(AppConfig.CommonAppFolder, _config.Input.ArchiveFolder);
                }

                // Create archive folder if it doesn't exist
                if (!Directory.Exists(_archiveFolderPath))
                {
                    Directory.CreateDirectory(_archiveFolderPath);
                    Log.Information("Created archive folder: {ArchiveFolder}", _archiveFolderPath);
                }

                // Get error folder path
                if (Path.IsPathRooted(_config.Input.ErrorFolder))
                {
                    _errorFolderPath = _config.Input.ErrorFolder;
                }
                else
                {
                    _errorFolderPath = Path.Combine(AppConfig.CommonAppFolder, _config.Input.ErrorFolder);
                }

                // Create error folder if it doesn't exist
                if (!Directory.Exists(_errorFolderPath))
                {
                    Directory.CreateDirectory(_errorFolderPath);
                    Log.Information("Created error folder: {ErrorFolder}", _errorFolderPath);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error initializing folders: {Message}", ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Processes all DICOM files in the input folder
        /// </summary>
        public async Task ProcessInputFolderAsync()
        {
            _processingResults.Clear();

            try
            {
                // Get all DCM files (files with .dcm extension or no extension which could be DICOM)
                var files = Directory.GetFiles(_inputFolderPath)
                    .Where(f => {
                        var ext = Path.GetExtension(f).ToLowerInvariant();
                        return ext == ".dcm" || string.IsNullOrEmpty(ext);
                    })
                    .ToList();

                if (files.Count == 0)
                {
                    Log.Debug("No DICOM files found in input folder");
                    return;
                }

                Log.Information("Found {Count} potential DICOM files in input folder", files.Count);

                foreach (var filePath in files)
                {
                    try
                    {
                        await ProcessDicomFileAsync(filePath);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error processing file {FilePath}: {Message}", filePath, ex.Message);
                        MoveToErrorFolder(filePath);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing input folder: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Processes a single DICOM file
        /// </summary>
        private async Task ProcessDicomFileAsync(string filePath)
        {
            string fileName = Path.GetFileName(filePath);
            DicomFile dicomFile = null;

            try
            {
                // Try to load the DICOM file
                try
                {
                    dicomFile = await DicomFile.OpenAsync(filePath);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "File {FilePath} is not a valid DICOM file", filePath);
                    MoveToErrorFolder(filePath);
                    return;
                }

                // Get the SOP Instance UID (unique identifier for this DICOM instance)
                var sopInstanceUid = dicomFile.Dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, "");
                if (string.IsNullOrEmpty(sopInstanceUid))
                {
                    Log.Error("File {FilePath} does not contain a SOP Instance UID", filePath);
                    MoveToErrorFolder(filePath);
                    return;
                }

                // Check if we've already processed this SOP Instance
                if (CacheManager.IsAlreadySent(sopInstanceUid, CacheManager.CacheFolder))
                {
                    Log.Information("File with SOP Instance UID {SopInstanceUid} already processed, skipping", sopInstanceUid);
                    MoveToArchiveFolder(filePath);
                    return;
                }

                // Check if this is a retry
                if (RetryManager.IsPendingRetry(sopInstanceUid, CacheManager.CacheFolder))
                {
                    Log.Information("File with SOP Instance UID {SopInstanceUid} already in retry queue, skipping", sopInstanceUid);
                    MoveToArchiveFolder(filePath);
                    return;
                }

                // Create base ORU message
                string oruMessage = ORUGenerator.ReplacePlaceholders(_oruTemplate, dicomFile.Dataset);

                // --- Process potential embedded data based on SOP Class ---
                string sopClassUid = dicomFile.Dataset.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty);
                bool hasEmbeddedPdf = false;

                // Check for Encapsulated PDF Storage
                if (sopClassUid == DicomUID.EncapsulatedPDFStorage.UID)
                {
                    try
                    {
                        if (dicomFile.Dataset.Contains(DicomTag.EncapsulatedDocument))
                        {
                            var pdfDataElement = dicomFile.Dataset.GetDicomItem<DicomElement>(DicomTag.EncapsulatedDocument);
                            IByteBuffer pdfBuffer = pdfDataElement.Buffer;
                            byte[] pdfBytes = pdfBuffer.Data;

                            if (pdfBytes != null && pdfBytes.Length > 0)
                            {
                                string base64Pdf = Convert.ToBase64String(pdfBytes);
                                Log.Information("Extracted {ByteLength} bytes of embedded PDF data, converting to Base64", pdfBytes.Length);
                                // Reuse UpdateObxWithPdf logic, as it handles Base64 ED field construction
                                oruMessage = ORUGenerator.UpdateObxWithPdfFromData(oruMessage, base64Pdf);
                                hasEmbeddedPdf = true; // Mark that we found embedded PDF
                            }
                            else
                            {
                                Log.Warning("Encapsulated PDF DICOM {SopInstanceUid} contains empty EncapsulatedDocument tag", sopInstanceUid);
                            }
                        }
                        else
                        {
                            Log.Warning("Encapsulated PDF DICOM {SopInstanceUid} does not contain the EncapsulatedDocument tag", sopInstanceUid);
                        }
                    }
                    catch (Exception pdfEx)
                    {
                        Log.Error(pdfEx, "Error extracting or encoding embedded PDF data for {SopInstanceUid}: {Message}", sopInstanceUid, pdfEx.Message);
                    }
                }
                // Check for Secondary Capture Image Storage
                else if (sopClassUid == DicomUID.SecondaryCaptureImageStorage.UID) // Check if it's Secondary Capture
                {
                    try
                    {
                        var pixelData = DicomPixelData.Create(dicomFile.Dataset);
                        if (pixelData != null && pixelData.NumberOfFrames > 0)
                        {
                            var frame = pixelData.GetFrame(0);
                            byte[] imageBytes = GetBytesFromBuffer(frame);

                            if (imageBytes != null && imageBytes.Length > 0)
                            {
                                string base64Image = Convert.ToBase64String(imageBytes);
                                Log.Information("Extracted {ByteLength} bytes of pixel data for Secondary Capture, converting to Base64.", imageBytes.Length);
                                oruMessage = ORUGenerator.UpdateObxWithImageData(oruMessage, dicomFile.Dataset, base64Image);
                            }
                            else
                            {
                                Log.Warning("Could not extract bytes from pixel data frame for Secondary Capture {SopInstanceUid}", sopInstanceUid);
                            }
                        }
                        else
                        {
                            Log.Warning("Secondary Capture DICOM {SopInstanceUid} does not contain pixel data or frames.", sopInstanceUid);
                        }
                    }
                    catch (Exception imgEx)
                    {
                        Log.Error(imgEx, "Error extracting or encoding image data for Secondary Capture {SopInstanceUid}: {Message}", sopInstanceUid, imgEx.Message);
                    }
                }
                // --- End Process embedded data ---

                // Send the ORU message
                bool success = await HL7Sender.SendOruAsync(_config, oruMessage, _config.HL7.ReceiverHost, _config.HL7.ReceiverPort);

                if (success)
                {
                    // If successful, record in cache and remove from retry queue if it was there
                    CacheManager.SaveToCache(sopInstanceUid, oruMessage, CacheManager.CacheFolder);

                    if (RetryManager.IsPendingRetry(sopInstanceUid, CacheManager.CacheFolder))
                    {
                        RetryManager.RemovePendingMessage(sopInstanceUid, CacheManager.CacheFolder);
                        Log.Information("Successfully delivered previously failed message: {SopInstanceUid}", sopInstanceUid);
                    }

                    _processingResults.Add(new ProcessingResult
                    {
                        SopInstanceUid = sopInstanceUid,
                        FileName = fileName,
                        Success = true,
                        HasPdf = hasEmbeddedPdf // Use the flag set during PDF extraction
                    });

                    Log.Information("Successfully processed and sent ORU for {FileName}", fileName);
                }
                else
                {
                    // If failed, add to retry queue
                    int attemptCount = 1;
                    if (RetryManager.IsPendingRetry(sopInstanceUid, CacheManager.CacheFolder))
                    {
                        attemptCount = RetryManager.GetAttemptCount(sopInstanceUid, CacheManager.CacheFolder) + 1;
                    }

                    // Important: Save the potentially modified oruMessage (with embedded data) to retry
                    RetryManager.SavePendingMessage(sopInstanceUid, oruMessage, CacheManager.CacheFolder, attemptCount);

                    _processingResults.Add(new ProcessingResult
                    {
                        SopInstanceUid = sopInstanceUid,
                        FileName = fileName,
                        Success = false,
                        HasPdf = hasEmbeddedPdf // Use the flag set during PDF extraction
                    });

                    Log.Warning("Failed to send ORU for {FileName}, added to retry queue (attempt {AttemptCount})", fileName, attemptCount);
                }

                // Move the processed file to the archive folder regardless of send success
                MoveToArchiveFolder(filePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing DICOM file {FileName}: {Message}", fileName, ex.Message);
                // Ensure file is moved to error even if exception happens before MoveToArchive
                if (dicomFile != null) // Check if dicomFile was loaded before error
                    MoveToErrorFolder(filePath);
                else if(File.Exists(filePath)) // If loading failed, still try to move original
                    MoveToErrorFolder(filePath);
            }
        }

        /// <summary>
        /// Helper to extract bytes from various IByteBuffer types used in fo-dicom 5.x.
        /// </summary>
        private static byte[] GetBytesFromBuffer(IByteBuffer buffer)
        {
            if (buffer is null) return null;

            // In fo-dicom 5, both 8-bit and 16-bit data are often in MemoryByteBuffer
            if (buffer is MemoryByteBuffer memoryBuffer)
            {
                return memoryBuffer.Data;
            }
            // Handle cases where data might be file-backed
            else if (buffer is FileByteBuffer fileBuffer)
            {
                Log.Warning("Pixel data frame is FileByteBuffer, reading all data.");
                // FileByteBuffer.Data reads the entire file content into memory
                return fileBuffer.Data;
            }
            // Fallback: Attempt to get data directly. This might cover other buffer types
            // or future implementations, but relies on the .Data property being available.
            else
            {
                Log.Warning("Pixel data buffer type ({FrameType}) not MemoryByteBuffer or FileByteBuffer. Attempting direct .Data access.", buffer.GetType().Name);
                try
                {
                    // The .Data property exists on IByteBuffer interface, should work.
                    return buffer.Data;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to get data from buffer type {BufferType}", buffer.GetType().Name);
                    return null;
                }
            }
        }

        /// <summary>
        /// Moves a file to the archive folder
        /// </summary>
        private void MoveToArchiveFolder(string filePath)
        {
            try
            {
                string fileName = Path.GetFileName(filePath);
                string archivePath = Path.Combine(_archiveFolderPath, fileName);

                // If file already exists in archive, add timestamp to make it unique
                if (File.Exists(archivePath))
                {
                    string extension = Path.GetExtension(fileName);
                    string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                    string timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                    archivePath = Path.Combine(_archiveFolderPath, $"{nameWithoutExt}_{timestamp}{extension}");
                }

                File.Move(filePath, archivePath);
                Log.Debug("Moved file to archive: {SourcePath} -> {DestPath}", filePath, archivePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error moving file to archive: {FilePath}, {Message}", filePath, ex.Message);
                // Don't throw - we want to continue processing other files
            }
        }

        /// <summary>
        /// Moves a file to the error folder
        /// </summary>
        private void MoveToErrorFolder(string filePath)
        {
            try
            {
                string fileName = Path.GetFileName(filePath);
                string errorPath = Path.Combine(_errorFolderPath, fileName);

                // If file already exists in error folder, add timestamp to make it unique
                if (File.Exists(errorPath))
                {
                    string extension = Path.GetExtension(fileName);
                    string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                    string timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                    errorPath = Path.Combine(_errorFolderPath, $"{nameWithoutExt}_{timestamp}{extension}");
                }

                File.Move(filePath, errorPath);
                Log.Debug("Moved file to error folder: {SourcePath} -> {DestPath}", filePath, errorPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error moving file to error folder: {FilePath}, {Message}", filePath, ex.Message);
                // Don't throw - we want to continue processing other files
            }
        }

        /// <summary>
        /// Gets the results of the last processing run
        /// </summary>
        public IEnumerable<ProcessingResult> GetProcessingResults() => _processingResults.AsEnumerable();

        /// <summary>
        /// Processes pending messages from the retry queue
        /// </summary>
        public void ProcessPendingMessages()
        {
            var cutoffTime = DateTime.UtcNow.AddMinutes(-_config.Retry.RetryIntervalMinutes);
            var pendingMessages = RetryManager.GetPendingMessages<PendingOruMessage>(CacheManager.CacheFolder, cutoffTime,
                (messageId, messageContent, attemptCount) => new PendingOruMessage
                {
                    SopInstanceUid = messageId,
                    OruMessage = messageContent,
                    AttemptCount = attemptCount
                });

            foreach (var pending in pendingMessages)
            {
                Log.Information("Retrying ORU for {SopInstanceUid}, attempt {AttemptCount}", pending.SopInstanceUid, pending.AttemptCount);

                bool success = HL7Sender.SendOruAsync(_config, pending.OruMessage, _config.HL7.ReceiverHost, _config.HL7.ReceiverPort).Result;

                if (success)
                {
                    Log.Information("Successfully delivered previously failed message: {SopInstanceUid}", pending.SopInstanceUid);
                    RetryManager.RemovePendingMessage(pending.SopInstanceUid, CacheManager.CacheFolder);
                }
                else
                {
                    Log.Warning("Failed to deliver {SopInstanceUid}, will retry again (attempt {AttemptCount})", pending.SopInstanceUid, pending.AttemptCount);
                    RetryManager.SavePendingMessage(pending.SopInstanceUid, pending.OruMessage, CacheManager.CacheFolder, pending.AttemptCount + 1);
                }
            }
        }
    }

    /// <summary>
    /// Represents the result of processing a DICOM file
    /// </summary>
    public class ProcessingResult
    {
        public string SopInstanceUid { get; set; }
        public string FileName { get; set; }
        public bool Success { get; set; }
        public bool HasPdf { get; set; } // Keep this to indicate if embedded PDF was found
    }
}
