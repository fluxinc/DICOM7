using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DICOM7.Shared;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;
using FellowOakDicom.Network;
using Microsoft.Extensions.Logging;

namespace DICOM7.DICOM2ORU
{
  internal class StoreSCP
    : BasicSCP, IDicomCStoreProvider, IMakeIdentifiers
  {
    public StoreSCP(
      INetworkStream stream,
      Encoding fallBackEncoding,
      ILogger logger,
      DicomServiceDependencies dependencies,
      string aeTitle)
      : base(stream, fallBackEncoding, logger, dependencies, aeTitle)
    {
    }

    public delegate void AfterItemStoredEventHandler(object sender, AfterItemStoredEventArgs ea);

    public delegate void BeforeItemStoredEventHandler(object sender, BeforeItemStoredEventArgs ea);


    // Static fields
    private static int ReceivingCount { get; set; }

    // ReSharper disable once MemberCanBeProtected.Global

    private static readonly DicomTransferSyntax[] AcceptedImageTransferSyntaxes =
    {
      // Lossless
      DicomTransferSyntax.JPEG2000Lossless,
      DicomTransferSyntax.JPEGLSLossless,
      DicomTransferSyntax.JPEGProcess14,
      DicomTransferSyntax.JPEGProcess14SV1,
      DicomTransferSyntax.RLELossless,

      // Lossy
      DicomTransferSyntax.JPEG2000Lossy,
      DicomTransferSyntax.JPEGLSNearLossless,
      DicomTransferSyntax.JPEGProcess1,
      DicomTransferSyntax.JPEGProcess2_4,

      // Uncompressed
      DicomTransferSyntax.ExplicitVRBigEndian,
      DicomTransferSyntax.ExplicitVRLittleEndian,
      DicomTransferSyntax.ImplicitVRBigEndian,
      DicomTransferSyntax.ImplicitVRLittleEndian
    };

    // fo-dicom does not pre-define the following DicomStatus for us, so we define it ourselves here,
    // but according to the DICOM standard page here it should be a legal response for a C-STORE
    // request: https://dicom.nema.org/medical/dicom/current/output/chtml/part07/chapter_9.html
    private static readonly DicomStatus DicomStatusRefusedNotAuthorized =
      new DicomStatus("0124", DicomState.Failure, "Refused: Not Authorized.");

    // DicomImageProcessor and ORU template for processing
    private static DicomImageProcessor _imageProcessor;
    private static string _oruTemplate;
    private static Config _config;

    // Set processors needed for ORU generation
    public static void SetProcessors(DicomImageProcessor imageProcessor, string oruTemplate, Config config)
    {
      _imageProcessor = imageProcessor;
      _oruTemplate = oruTemplate;
      _config = config;
    }

    public async Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
    {
      _logger.LogInformation($"C-Store request received from '{LastAssociatedAeTitle}'");
      ReceivingCount++;
      try
      {
        return await HandleCStoreRequestAsync(request);
      }
      finally
      {
        ReceivingCount--;
      }
    }

    public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
    {
      _logger.LogWarning($"C-Store request exception: {e}");
      // let it be logged
      return Task.CompletedTask;
    }

    public string GetIdentifier(DicomRequest request)
    {
      if (request is null) throw new ArgumentNullException(nameof(request));

      string sopInstanceUID = request.Dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID);
      sopInstanceUID = sopInstanceUID.Replace('_', '.');
      string studyInstanceUID = request.Dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID);

      studyInstanceUID = string.IsNullOrWhiteSpace(studyInstanceUID)
        ? sopInstanceUID
        : studyInstanceUID.Replace('_', '.');

      return $"{studyInstanceUID}__{sopInstanceUID}";
    }

    public static event AfterItemStoredEventHandler DefaultAfterItemStored;
    public static event BeforeItemStoredEventHandler DefaultBeforeItemStored;
    protected event AfterItemStoredEventHandler AfterItemStored = DefaultAfterItemStored;
    protected event BeforeItemStoredEventHandler BeforeItemStored = DefaultBeforeItemStored;

    private DicomCStoreResponse CreateDicomCStoreResponse(DicomCStoreRequest request, DicomStatus status)
    {
      Logger.LogDebug($"CreateDicomCStoreResponse({request}, {status})");
      DicomCStoreResponse response = new DicomCStoreResponse(request, status);

      try
      {
        if (request.Command == null)
        {
          _logger.LogWarning("WARNING: Request command dataset is null, this is abnormal.");

          return response;
        }

        string affectedSOPInstanceUID = request.Command.GetSingleValue<string>(DicomTag.AffectedSOPInstanceUID);

        if (string.IsNullOrEmpty(affectedSOPInstanceUID))
        {
          _logger.LogWarning(
            "WARNING: Request command dataset constains null/empty AffectedSOPInstanceUID, this is abnormal.");

          return response;
        }

        if (response.Command == null)
        {
          _logger.LogWarning("WARNING: Response command dataset is null, this is abnormal.");

          return response;
        }

        response.Command.AddOrUpdate(
          DicomTag.AffectedSOPInstanceUID,
          affectedSOPInstanceUID
        );
      }
      catch (Exception e)
      {
        _logger.LogWarning($"Unexpected exception during CreateDicomCStoreResponse: {e}\r\n{e.StackTrace}");
      }

      return response;
    }

    private static readonly Dictionary<string, (bool AcceptDuplicateItem, DicomStatus ResponseStatus)>
      DuplicateSOPInstanceBehaviors =
        new Dictionary<string, (bool AcceptDuplicateItem, DicomStatus ResponseStatus)>
        {
          { "ignore", (AcceptDuplicateItem: false, ResponseStatus: DicomStatus.Success) },
          { "reject", (AcceptDuplicateItem: false, ResponseStatus: DicomStatusRefusedNotAuthorized) },
          { "report", (AcceptDuplicateItem: false, ResponseStatus: DicomStatus.DuplicateSOPInstance) },
          // Note that the ResponseStatus field of this next row will not actually be used directly: because AcceptDuplicateItem is true,
          // we will end up falling through to a case responding with Success anyhow.
          { "accept", (AcceptDuplicateItem: true, ResponseStatus: DicomStatus.Success) }
        };

    private async Task<DicomCStoreResponse> HandleCStoreRequestAsync(DicomCStoreRequest request)
    {
      if (request is null) throw new ArgumentNullException(nameof(request));

      try
      {
        DateTime receivedAtUtc = DateTime.UtcNow;

        // Get the SOP Instance UID for tracking
        string sopInstanceUid = request.Dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, "");

        if (string.IsNullOrEmpty(sopInstanceUid))
        {
          _logger.LogError("DICOM dataset missing SOP Instance UID");
          return CreateDicomCStoreResponse(request, DicomStatus.ProcessingFailure);
        }

        // Check if this DICOM has already been processed
        if (CacheManager.IsAlreadySent(sopInstanceUid))
        {
          _logger.LogInformation("DICOM with SOP Instance UID {SopInstanceUid} already processed", sopInstanceUid);
          return CreateDicomCStoreResponse(request, DicomStatus.Success);
        }

        // Also check if it's already in the retry queue
        if (RetryManager.IsPendingRetry(sopInstanceUid, CacheManager.CacheFolder))
        {
          _logger.LogInformation("DICOM with SOP Instance UID {SopInstanceUid} already in retry queue", sopInstanceUid);
          return CreateDicomCStoreResponse(request, DicomStatus.Success);
        }

        // Save to temp file for processing
        string tempFilePath = Path.GetTempFileName();

        try
        {
          // Save DICOM file temporarily for processing
          await request.File.SaveAsync(tempFilePath);

          if (!Validate(tempFilePath))
          {
            _logger.LogError("DICOM file validation failed for SOP Instance UID {SopInstanceUid}", sopInstanceUid);
            File.Delete(tempFilePath);
            return CreateDicomCStoreResponse(request, DicomStatus.ProcessingFailure);
          }

          // Generate ORU message from the DICOM dataset
          string oruMessage = ORUGenerator.ReplacePlaceholders(_oruTemplate, request.Dataset);

          // Process potential embedded data (images, PDFs, etc.) if processor is available
          if (_imageProcessor != null)
            try
            {
              // Extract the SOP Class UID to determine the type of DICOM file
              string sopClassUid = request.Dataset.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty);

              // Check for Encapsulated PDF Storage
              if (sopClassUid == DicomUID.EncapsulatedPDFStorage.UID)
              {
                if (request.Dataset.Contains(DicomTag.EncapsulatedDocument))
                {
                  DicomElement pdfDataElement =
                    request.Dataset.GetDicomItem<DicomElement>(DicomTag.EncapsulatedDocument);
                  byte[] pdfBytes = pdfDataElement.Buffer.Data;

                  if (pdfBytes != null && pdfBytes.Length > 0)
                  {
                    string base64Pdf = Convert.ToBase64String(pdfBytes);
                    _logger.LogInformation("Extracted embedded PDF data, converting to Base64");
                    oruMessage = ORUGenerator.UpdateObxWithPdfFromData(oruMessage, base64Pdf);
                  }
                }
              }
              // Check for Secondary Capture Image Storage
              else if (sopClassUid == DicomUID.SecondaryCaptureImageStorage.UID)
              {
                DicomPixelData pixelData = DicomPixelData.Create(request.Dataset);
                if (pixelData != null && pixelData.NumberOfFrames > 0)
                {
                  IByteBuffer frame = pixelData.GetFrame(0);
                  byte[] imageBytes = frame.Data;

                  if (imageBytes != null && imageBytes.Length > 0)
                  {
                    string base64Image = Convert.ToBase64String(imageBytes);
                    _logger.LogInformation("Extracted pixel data for Secondary Capture, converting to Base64");
                    oruMessage = ORUGenerator.UpdateObxWithImageData(oruMessage, request.Dataset, base64Image);
                  }
                }
              }
            }
            catch (Exception ex)
            {
              _logger.LogError(ex, "Error processing embedded data: {Message}", ex.Message);
              // Continue with the basic ORU message
            }

          // Ensure the outgoing folder exists in cache
          CacheManager.EnsureCacheFolder();

          // Create outgoing folder if it doesn't exist
          string outgoingFolder = Path.Combine(CacheManager.CacheFolder, "outgoing");
          if (!Directory.Exists(outgoingFolder))
          {
            Directory.CreateDirectory(outgoingFolder);
            _logger.LogInformation("Created outgoing ORU folder: {OutgoingPath}", outgoingFolder);
          }

          // Save the ORU message to the outgoing folder
          string oruFilePath = Path.Combine(outgoingFolder, $"{sopInstanceUid}.oru");
          File.WriteAllText(oruFilePath, oruMessage);
          _logger.LogInformation("Saved ORU message to outgoing folder: {FilePath}", oruFilePath);

          // Mark DICOM as processed, storing the ORU message for reference
          CacheManager.MarkAsProcessed(sopInstanceUid, _config.Cache.KeepSentItems, oruMessage);

          // Cleanup temporary DICOM file
          File.Delete(tempFilePath);

          return CreateDicomCStoreResponse(request, DicomStatus.Success);
        }
        catch (Exception ex)
        {
          _logger.LogError(ex, "Error processing DICOM for ORU generation: {Message}", ex.Message);

          // Clean up the temp file if it exists
          try
          {
            if (File.Exists(tempFilePath)) File.Delete(tempFilePath);
          }
          catch
          {
            /* ignore cleanup errors */
          }

          // Try to add to retry queue if there was an error
          try
          {
            string oruMessage = ORUGenerator.ReplacePlaceholders(_oruTemplate, request.Dataset);
            RetryManager.SavePendingMessage(sopInstanceUid, oruMessage, CacheManager.CacheFolder);
            _logger.LogInformation("Added failed ORU to retry queue: {SopInstanceUid}", sopInstanceUid);
          }
          catch (Exception retryEx)
          {
            _logger.LogError(retryEx, "Failed to add to retry queue: {Message}", retryEx.Message);
          }

          return CreateDicomCStoreResponse(request, DicomStatus.ProcessingFailure);
        }
      }
      catch (Exception e)
      {
        _logger.LogError($"Error during HandleCStoreRequestAsync: {e}");
        return CreateDicomCStoreResponse(request, DicomStatus.ProcessingFailure);
      }
    }

    private bool Validate(string temporaryName)
    {
      _logger.LogDebug($"Validate({temporaryName})");
      bool valid;
      try
      {
        DicomFile temp = DicomFile.Open(temporaryName);
        valid = temp.Dataset.Any();
      }
      catch (Exception e)
      {
        valid = false;
        _logger.LogError($"Error validating DICOM file: {e}\r\n{e.StackTrace}");
      }

      return valid;
    }

    protected override void HandlePresentContexts(DicomAssociation association)
    {
      _logger.LogDebug($"HandlePresentContexts({association})");
      foreach (DicomPresentationContext pc in association.PresentationContexts)
        if (pc.AbstractSyntax == DicomUID.Verification)
          pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes);
        else if (pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None)
          pc.AcceptTransferSyntaxes(AcceptedImageTransferSyntaxes);
    }

    public class AfterItemStoredEventArgs : EventArgs
    {
      public string DcmFilePath { get; }
      public DicomStatus DicomStatus { get; set; } = DicomStatus.Success;
      public DicomCStoreRequest Request { get; }

      public AfterItemStoredEventArgs(DicomCStoreRequest request, string dcmFilePath)
      {
        DcmFilePath = dcmFilePath;
        Request = request;
      }
    }

    public class BeforeItemStoredEventArgs : EventArgs
    {
      public DicomStatus DicomStatus { get; set; } = null;
      public DicomCStoreRequest Request { get; }

      public BeforeItemStoredEventArgs(DicomCStoreRequest request) => Request = request;
    }
  }
}
