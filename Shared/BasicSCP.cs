using System;
using System.Text;
using System.Threading.Tasks;
using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.Logging;

namespace DICOM7.Shared;

internal abstract class BasicSCP : DicomService, IDicomServiceProvider, IDicomCEchoProvider
{
  protected readonly ILogger _logger;
  protected static readonly DicomTransferSyntax[] AcceptedTransferSyntaxes =
  [
    DicomTransferSyntax.ImplicitVRLittleEndian,
    DicomTransferSyntax.ExplicitVRLittleEndian,
    DicomTransferSyntax.ExplicitVRBigEndian,
  ];

  internal string LastAssociatedAeTitle { get; set; }


  protected BasicSCP(
    INetworkStream stream,
    Encoding fallBackEncoding,
    ILogger logger,
    DicomServiceDependencies dependencies,
    string aeTitle
    ) : base(stream, fallBackEncoding, logger, dependencies)
  {
    _logger = logger;
    Options = new DicomServiceOptions
    {
      IgnoreUnsupportedTransferSyntaxChange = true,
    };

    AeTitle = aeTitle;
  }

  public string AeTitle { get; set; }
  public bool StrictCalledAe { get; set; }

  public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
  {
    _logger.LogInformation($"Responding to C-Echo request from '{LastAssociatedAeTitle}'");

    return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
  }

  public void OnConnectionClosed(Exception exception)
  {
    // fo-dicom will always print "Connection closed" so we don't actually need to do this:
    //_logger.LogInformation("Connection closed.");
    // If we did, it would just double the number of such messages in the log.
  }

  public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
  {
    _logger.LogWarning($"Association with '{LastAssociatedAeTitle}' aborted");
  }

  public Task OnReceiveAssociationReleaseRequestAsync()
  {
    try
    {
      _logger.LogInformation($"Association release request received from '{LastAssociatedAeTitle}'");
      return SendAssociationReleaseResponseAsync();
    }
    catch (ObjectDisposedException e)
    {
      _logger.LogInformation(
          $"ObjectDisposedException in OnReceiveAssociationReleaseRequest: '{e.Message}'\r\n{e.StackTrace}");
      return Task.CompletedTask;
    }
  }

  public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
  {
    _logger.LogInformation($"Association request received from '{association.CallingAE}' for '{association.CalledAE}'");

    try
    {
      LastAssociatedAeTitle = association.CallingAE;


      if (StrictCalledAe && association.CalledAE != AeTitle)
      {
        _logger.LogWarning($"Invalid called AE '{association.CalledAE}', expected '{AeTitle}'. Rejecting association");
        return SendAssociationRejectAsync(
            DicomRejectResult.Permanent,
            DicomRejectSource.ServiceUser,
            DicomRejectReason.CalledAENotRecognized);
      }

      string calledAE = association.CalledAE;

      if (calledAE != AeTitle)
      {
        _logger.LogInformation($"Rejecting association for '{association.CalledAE}', node or alias not found.");
        return SendAssociationRejectAsync(
        DicomRejectResult.Permanent,
        DicomRejectSource.ServiceUser,
        DicomRejectReason.CalledAENotRecognized);
      }

      HandlePresentContexts(association);

      _logger.LogInformation("Accepting association...");

      return SendAssociationAcceptAsync(association);
    }
    catch (ObjectDisposedException e)
    {
      _logger.LogError($"ObjectDisposedException in OnReceiveAssociationRequest: '{e.Message}'\r\n{e.StackTrace}");
      return SendAssociationRejectAsync(DicomRejectResult.Permanent, DicomRejectSource.ServiceUser, DicomRejectReason.NoReasonGiven_);
    }
  }

  protected abstract void HandlePresentContexts(DicomAssociation association);
}

internal interface IMakeIdentifiers
{
  string GetIdentifier(DicomRequest request);
}
