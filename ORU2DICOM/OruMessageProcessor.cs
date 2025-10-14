using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DICOM7.Shared;
using FellowOakDicom;
using FellowOakDicom.Network;
using Serilog;

namespace DICOM7.ORU2DICOM
{
  /// <summary>
  /// Coordinates ORU parsing, DICOM generation, persistence, and retry handling
  /// </summary>
  public class OruMessageProcessor
  {
    private static readonly ILogger Logger = Log.ForContext<OruMessageProcessor>();

    private readonly Config _config;
    private readonly DicomSender _dicomSender;
    private readonly object _pendingLock = new object();
    private volatile bool _processingPending;

    public OruMessageProcessor(Config config)
    {
      _config = config ?? throw new ArgumentNullException(nameof(config));
      _dicomSender = new DicomSender(config);
    }

    public async Task<OruProcessingResult> HandleIncomingAsync(string hl7Message, CancellationToken cancellationToken)
    {
      CachedORU cachedOru;

      try
      {
        cachedOru = new CachedORU(hl7Message);
      }
      catch (Exception ex)
      {
        Logger.Error(ex, "Failed to parse incoming ORU payload");
        return OruProcessingResult.Failure("Unable to parse ORU message");
      }

      if (CacheManager.IsAlreadyProcessed(cachedOru.UUID))
      {
        Logger.Information("Duplicate ORU message {MessageId} ignored", cachedOru.UUID);
        return OruProcessingResult.AlreadyProcessed(cachedOru);
      }

      CacheManager.SaveIncomingMessage(cachedOru);

      return await ProcessAsync(cachedOru, cancellationToken, true, 1).ConfigureAwait(false);
    }

    public async Task ProcessPendingMessagesAsync(CancellationToken cancellationToken)
    {
      if (_processingPending)
      {
        Logger.Debug("Retry sweep already in progress");
        return;
      }

      lock (_pendingLock)
      {
        if (_processingPending)
        {
          return;
        }

        _processingPending = true;
      }

      try
      {
        DateTime cutoff = DateTime.Now.AddMinutes(-Math.Max(1, _config.Retry.RetryIntervalMinutes));
        IEnumerable<PendingMessage> pendingMessages = RetryManager.GetPendingMessages(
          CacheManager.CacheFolder,
          cutoff,
          delegate(string id, string content, int attempt)
          {
            return new PendingMessage
            {
              MessageId = id,
              Hl7 = content,
              Attempt = attempt
            };
          });

        foreach (PendingMessage pending in pendingMessages.ToList())
        {
          cancellationToken.ThrowIfCancellationRequested();

          CachedORU cachedOru;
          try
          {
            cachedOru = new CachedORU(pending.Hl7, pending.MessageId);
          }
          catch (Exception ex)
          {
            Logger.Error(ex, "Failed to parse pending ORU message {MessageId}; moving to error", pending.MessageId);
            CacheManager.MoveMessageToError(pending.MessageId, pending.Hl7, "Parse failure on retry: " + ex.Message);
            RetryManager.RemovePendingMessage(pending.MessageId, CacheManager.CacheFolder);
            continue;
          }

          CacheManager.EnsureIncomingMessageExists(cachedOru);

          OruProcessingResult result = await ProcessAsync(cachedOru, cancellationToken, false, pending.Attempt).ConfigureAwait(false);

          if (result.Status == OruProcessingStatus.Success || result.Status == OruProcessingStatus.AlreadyProcessed)
          {
            RetryManager.RemovePendingMessage(pending.MessageId, CacheManager.CacheFolder);
          }
          else if (result.Status == OruProcessingStatus.Deferred)
          {
            int nextAttempt = pending.Attempt + 1;
            if (_config.Retry.MaxAttempts > 0 && nextAttempt > _config.Retry.MaxAttempts)
            {
              Logger.Error("Max retry attempts exceeded for ORU message {MessageId}; archiving to error", pending.MessageId);
              CacheManager.MoveMessageToError(pending.MessageId, cachedOru.Text, "Exceeded retry attempts");
              RetryManager.RemovePendingMessage(pending.MessageId, CacheManager.CacheFolder);
            }
            else
            {
              RetryManager.SavePendingMessage(pending.MessageId, cachedOru.Text, CacheManager.CacheFolder, nextAttempt);
            }
          }
          else
          {
            RetryManager.RemovePendingMessage(pending.MessageId, CacheManager.CacheFolder);
          }
        }
      }
      finally
      {
        _processingPending = false;
      }
    }

    private async Task<OruProcessingResult> ProcessAsync(CachedORU cachedOru, CancellationToken cancellationToken, bool addToRetryOnFailure, int attempt)
    {
      List<(DicomFile File, string Description)> dicomArtifacts = new List<(DicomFile, string)>();
      List<string> artifactPaths = new List<string>();

      try
      {
        DicomDataset srDataset = cachedOru.AsDicomDataset(_config.Dicom.DefaultStudyDescription);
        if (srDataset == null)
        {
          CacheManager.MoveMessageToError(cachedOru.UUID, cachedOru.Text, "Conversion returned null dataset", artifactPaths);
          return OruProcessingResult.Failure("ORU conversion returned no dataset");
        }

        DicomFile srFile = new DicomFile(srDataset);
        string srPath = CacheManager.SaveDicomFile(cachedOru.UUID, srFile);
        dicomArtifacts.Add((srFile, "structured report"));
        artifactPaths.Add(srPath);

        if (cachedOru.TryGetPdfAttachment(out CachedORU.PdfAttachment pdfAttachment))
        {
          try
          {
            DicomDataset pdfDataset = cachedOru.BuildEncapsulatedPdfDataset(pdfAttachment, _config.Dicom.DefaultStudyDescription);
            DicomFile pdfFile = new DicomFile(pdfDataset);
            string pdfPath = CacheManager.SaveDicomFile(cachedOru.UUID, pdfFile, "pdf");
            dicomArtifacts.Add((pdfFile, "encapsulated PDF"));
            artifactPaths.Add(pdfPath);
            Logger.Information("Detected PDF attachment in ORU message {MessageId}; will transmit as Encapsulated PDF", cachedOru.UUID);
          }
          catch (Exception pdfEx)
          {
            Logger.Error(pdfEx, "Failed to build Encapsulated PDF dataset for {MessageId}", cachedOru.UUID);
            CacheManager.MoveMessageToError(cachedOru.UUID, cachedOru.Text, "Failed to build Encapsulated PDF: " + pdfEx.Message, artifactPaths);
            return OruProcessingResult.Failure("Unable to convert embedded PDF");
          }
        }
      }
      catch (Exception ex)
      {
        Logger.Error(ex, "ORU to DICOM conversion failed for {MessageId}", cachedOru.UUID);
        CacheManager.MoveMessageToError(cachedOru.UUID, cachedOru.Text, "Conversion failure: " + ex.Message, artifactPaths);
        return OruProcessingResult.Failure("Unable to convert ORU to DICOM");
      }

      DicomSendResult lastResult = null;

      foreach ((DicomFile File, string Description) artifact in dicomArtifacts)
      {
        DicomSendResult sendResult = await _dicomSender.SendAsync(artifact.File, cancellationToken).ConfigureAwait(false);

        if (!sendResult.Success)
        {
          Logger.Warning("Deferred ORU message {MessageId} while sending {ArtifactDescription}: {Reason}", cachedOru.UUID, artifact.Description, sendResult.ErrorMessage);

          if (addToRetryOnFailure)
          {
            RetryManager.SavePendingMessage(cachedOru.UUID, cachedOru.Text, CacheManager.CacheFolder, attempt);
          }

          return OruProcessingResult.Deferred(cachedOru, sendResult.ErrorMessage);
        }

        lastResult = sendResult;
      }

      CacheManager.MarkAsProcessed(cachedOru.UUID, _config.Cache.KeepSentItems, cachedOru.Text, _config.Cache.PersistDicomFiles, artifactPaths);
      Logger.Information("Delivered ORU message {MessageId} (attempt {Attempt}) with {Count} DICOM object(s)", cachedOru.UUID, attempt, dicomArtifacts.Count);

      return OruProcessingResult.Success(cachedOru, lastResult?.Status ?? DicomStatus.Success);
    }

    private class PendingMessage
    {
      public string MessageId { get; set; }
      public string Hl7 { get; set; }
      public int Attempt { get; set; }
    }
  }

  public enum OruProcessingStatus
  {
    Success,
    Deferred,
    Failure,
    AlreadyProcessed
  }

  public class OruProcessingResult
  {
    public OruProcessingStatus Status { get; private set; }
    public CachedORU Message { get; private set; }
    public DicomStatus DicomStatus { get; private set; }
    public string ErrorMessage { get; private set; }

    public bool ShouldAckSuccess
    {
      get
      {
        return Status == OruProcessingStatus.Success
          || Status == OruProcessingStatus.Deferred
          || Status == OruProcessingStatus.AlreadyProcessed;
      }
    }

    public static OruProcessingResult Success(CachedORU message, DicomStatus status)
    {
      return new OruProcessingResult
      {
        Status = OruProcessingStatus.Success,
        Message = message,
        DicomStatus = status,
        ErrorMessage = string.Empty
      };
    }

    public static OruProcessingResult Deferred(CachedORU message, string reason)
    {
      return new OruProcessingResult
      {
        Status = OruProcessingStatus.Deferred,
        Message = message,
        ErrorMessage = reason ?? string.Empty
      };
    }

    public static OruProcessingResult Failure(string reason)
    {
      return new OruProcessingResult
      {
        Status = OruProcessingStatus.Failure,
        ErrorMessage = reason ?? string.Empty
      };
    }

    public static OruProcessingResult AlreadyProcessed(CachedORU message)
    {
      return new OruProcessingResult
      {
        Status = OruProcessingStatus.AlreadyProcessed,
        Message = message,
        ErrorMessage = string.Empty
      };
    }
  }
}
