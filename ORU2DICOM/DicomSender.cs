using System;
using System.Threading;
using System.Threading.Tasks;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Serilog;

namespace DICOM7.ORU2DICOM
{
  /// <summary>
  /// Handles C-STORE transmission of generated DICOM payloads
  /// </summary>
  public class DicomSender
  {
    private static readonly ILogger Logger = Log.ForContext<DicomSender>();
    private readonly Config _config;

    public DicomSender(Config config)
    {
      _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public async Task<DicomSendResult> SendAsync(DicomFile dicomFile, CancellationToken cancellationToken)
    {
      if (dicomFile == null)
      {
        throw new ArgumentNullException(nameof(dicomFile));
      }

      IDicomClient client = DicomClientFactory.Create(
        _config.Dicom.DestinationHost,
        _config.Dicom.DestinationPort,
        _config.Dicom.UseTls,
        _config.Dicom.SourceAeTitle,
        _config.Dicom.DestinationAeTitle);

      DicomSendResult result = DicomSendResult.CreateFailure(DicomStatus.UnrecognizedOperation, "No C-STORE response received");

      DicomCStoreRequest request = new DicomCStoreRequest(dicomFile);
      request.OnResponseReceived += delegate(DicomCStoreRequest req, DicomCStoreResponse response)
      {
        bool success = response.Status.State == DicomState.Success
          || response.Status == DicomStatus.Success;

        result = success
          ? DicomSendResult.CreateSuccess(response.Status)
          : DicomSendResult.CreateFailure(response.Status, response.Status.Description);
      };

      Logger.Information(
        "Sending C-STORE for SOP {SopInstance} to {Host}:{Port} (AE={Ae})",
        request.SOPInstanceUID.UID,
        _config.Dicom.DestinationHost,
        _config.Dicom.DestinationPort,
        _config.Dicom.DestinationAeTitle);

      try
      {
        await client.AddRequestAsync(request).ConfigureAwait(false);
        await client.SendAsync(cancellationToken, DicomClientCancellationMode.ImmediatelyAbortAssociation).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        Logger.Warning("C-STORE operation cancelled for SOP {SopInstance}", request.SOPInstanceUID.UID);
        result = DicomSendResult.CreateFailure(DicomStatus.Cancel, "Send cancelled");
      }
      catch (Exception ex)
      {
        Logger.Error(ex, "C-STORE failed for SOP {SopInstance}", request.SOPInstanceUID.UID);
        result = DicomSendResult.CreateFailure(DicomStatus.ProcessingFailure, ex.Message);
      }

      return result;
    }
  }

  public class DicomSendResult
  {
    public bool Success { get; private set; }
    public DicomStatus Status { get; private set; }
    public string ErrorMessage { get; private set; }

    private DicomSendResult()
    {
    }

    public static DicomSendResult CreateSuccess(DicomStatus status)
    {
      return new DicomSendResult
      {
        Success = true,
        Status = status,
        ErrorMessage = string.Empty
      };
    }

    public static DicomSendResult CreateFailure(DicomStatus status, string errorMessage)
    {
      return new DicomSendResult
      {
        Success = false,
        Status = status,
        ErrorMessage = errorMessage ?? string.Empty
      };
    }
  }
}
