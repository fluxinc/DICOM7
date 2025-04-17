using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DICOM7.Shared;
using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;

namespace DICOM7.DICOM2ORU;

internal class StoreSCP(
  INetworkStream stream,
  Encoding fallBackEncoding,
  ILogger logger,
  DicomServiceDependencies dependencies,
  string aeTitle)
  : BasicSCP(stream, fallBackEncoding, logger, dependencies, aeTitle), IDicomCStoreProvider, IMakeIdentifiers
{
    public delegate void AfterItemStoredEventHandler(object sender, AfterItemStoredEventArgs ea);

    public delegate void BeforeItemStoredEventHandler(object sender, BeforeItemStoredEventArgs ea);


    // Static fields
    private static int ReceivingCount { get; set; }

    // ReSharper disable once MemberCanBeProtected.Global

    private static readonly DicomTransferSyntax[] AcceptedImageTransferSyntaxes =
    [
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
    ];

    // fo-dicom does not pre-define the following DicomStatus for us, so we define it ourselves here,
    // but according to the DICOM standard page here it should be a legal response for a C-STORE
    // request: https://dicom.nema.org/medical/dicom/current/output/chtml/part07/chapter_9.html
    private static readonly DicomStatus DicomStatusRefusedNotAuthorized =
        new("0124", DicomState.Failure, "Refused: Not Authorized.");

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

        var sopInstanceUID = request.Dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID);
        sopInstanceUID = sopInstanceUID.Replace('_', '.');
        var studyInstanceUID = request.Dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID);

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
        var response = new DicomCStoreResponse(request, status);

        try
        {
            if (request.Command == null)
            {
                _logger.LogWarning("WARNING: Request command dataset is null, this is abnormal.");

                return response;
            }

            var affectedSOPInstanceUID = request.Command.GetSingleValue<string>(DicomTag.AffectedSOPInstanceUID);

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
            new()
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
        var receivedAtUtc = DateTime.UtcNow;

        using var cachedFile = new CachedFile(LastCalledAeTitle, LastAssociatedAeTitle, request.Dataset);

        // TODO: Save request.Dataset as outbound HL7 ORU into cache.  Might need to save to a temp file first.
        var temporaryName = GetTemporaryFileName(cachedFile);
        await request.File.SaveAsync(temporaryName);

        if (!Validate(temporaryName))
          return HandleInvalidFile(request, temporaryName);


        cachedFile.Log($"Wrote to {cachedFile.FileInfo.FullName}");

        return CreateDicomCStoreResponse(request, DicomStatus.Success);

      }
      catch (Exception e)
      {
        _logger.LogError($"Error during HandleCStoreRequestAsync: {e}");
        return CreateDicomCStoreResponse(request, DicomStatus.ProcessingFailure);
      }
    }

    private DicomCStoreResponse HandleInvalidFile(DicomCStoreRequest request, string temporaryName)
    {
        File.Delete(temporaryName);
        return CreateDicomCStoreResponse(request, DicomStatus.ProcessingFailure);
    }


    private bool Validate(string temporaryName)
    {
        _logger.LogDebug($"Validate({temporaryName})");
        bool valid;
        try
        {
            var temp = DicomFile.Open(temporaryName);
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
        foreach (var pc in association.PresentationContexts)
            if (pc.AbstractSyntax == DicomUID.Verification)
                pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes);
            else if (pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None)
                pc.AcceptTransferSyntaxes(AcceptedImageTransferSyntaxes);
    }

    public class AfterItemStoredEventArgs(DicomCStoreRequest request, string dcmFilePath) : EventArgs
    {
        public string DcmFilePath { get; } = dcmFilePath;
        public DicomStatus DicomStatus { get; set; } = DicomStatus.Success;
        public DicomCStoreRequest Request { get; } = request;
    }

    public class BeforeItemStoredEventArgs(DicomCStoreRequest request) : EventArgs
    {
        public DicomStatus DicomStatus { get; set; } = null;
        public DicomCStoreRequest Request { get; } = request;
    }
}
