using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Extensions.Logging;
using Serilog;

namespace OrderORM
{
  public class WorklistQuerier
  {
    private readonly Config _config;
    private readonly string _ormTemplate;
    private List<DicomDataset> _findResponses;
    private volatile DicomStatus _queryStatus;
    private readonly CancellationTokenSource _cancellationTokenSource;

    public WorklistQuerier(Config config, string ormTemplate)
    {
      _config = config;
      _ormTemplate = ormTemplate;
      _findResponses = new List<DicomDataset>();
      _cancellationTokenSource = new CancellationTokenSource();
    }

    private DicomDataset BuildQueryDataset()
    {
      var ds = new DicomDataset
      {
        { DicomTag.PatientName, "" },
        { DicomTag.PatientID, "" },
        { DicomTag.PatientBirthDate, "" },
        { DicomTag.PatientSex, "" },
        { DicomTag.StudyInstanceUID, "" },
        { DicomTag.AccessionNumber, "" },
        { DicomTag.ReferringPhysicianName, "" }
      };


      var sps = new DicomDataset();
      // Add ScheduledStationAETitle if configured
      if (!string.IsNullOrEmpty(_config.Query.ScheduledStationAeTitle))
      {
        sps.Add(DicomTag.ScheduledStationAETitle, _config.Query.ScheduledStationAeTitle);
      }

      // Add ScheduledProcedureStepStartDate
      sps.Add(DicomTag.ScheduledProcedureStepStartDate, GetScheduledDate(_config.Query.ScheduledProcedureStepStartDate));

      // Add Modality if configured
      if (!string.IsNullOrEmpty(_config.Query.Modality))
      {
        sps.Add(DicomTag.Modality, _config.Query.Modality);
      }

      ds.Add(DicomTag.ScheduledProcedureStepSequence, sps);

      return ds;
    }

    private string GetScheduledDate(DateConfig dateConfig)
    {
      if (dateConfig == null)
      {
        Log.Warning($"ScheduledProcedureStepStartDate configuration is missing, using today's date");
        return DateTime.Today.ToString("yyyyMMdd");
      }

      DateTime today = DateTime.Today;
      string mode = dateConfig.Mode?.ToLower() ?? "today";

      switch (mode)
      {
        case "today":
          return today.ToString("yyyyMMdd");
        case "range":
          int daysBefore = dateConfig.DaysBefore;
          int daysAfter = dateConfig.DaysAfter;
          DateTime start = today.AddDays(-daysBefore);
          DateTime end = today.AddDays(daysAfter);
          return $"{start:yyyyMMdd}-{end:yyyyMMdd}";
        case "specific":
          return string.IsNullOrEmpty(dateConfig.Date) ? "" : dateConfig.Date;
        default:
          Log.Warning("Invalid date mode `{DateConfigMode}`, using today\'s date", dateConfig.Mode);
          return today.ToString("yyyyMMdd");
      }
    }

    private IDicomClient CreateClient()
    {

      // Create a logger factory
      var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
      {
        builder.AddConsole();
      });

      return DicomClientFactory.Create(
          _config.Dicom.ScpHost,
          _config.Dicom.ScpPort,
          null,
          _config.Dicom.ScuAeTitle,
          _config.Dicom.ScpAeTitle
          );
    }

    private void OnFindResponseReceived(DicomCFindRequest request, DicomCFindResponse response)
    {
      if (response.Status == DicomStatus.Pending && response.HasDataset)
      {
        var dataset = response.Dataset;
        string studyInstanceUid = dataset.GetString(DicomTag.StudyInstanceUID);
        Log.Information("Found order with StudyInstanceUID: {StudyInstanceUid}", studyInstanceUid);
        _findResponses.Add(dataset);
      }
      else if (response.Status == DicomStatus.Success)
      {
        _queryStatus = DicomStatus.Success;
        Log.Information($"C-FIND query completed successfully");
      }
      else
      {
        _queryStatus = response.Status;
        Log.Information("C-FIND query status: 0x{StatusCode}", response.Status.Code);
      }
    }

    private async Task AddAndSendRequestAsync(DicomClient client, DicomRequest request, CancellationToken cancellationToken)
    {
      await client.AddRequestAsync(request);
      await client.SendAsync(cancellationToken, DicomClientCancellationMode.ImmediatelyAbortAssociation);
    }

    private bool WaitForQueryCompletion(int timeoutSeconds = 60)
    {
      var startTime = DateTime.UtcNow;
      while (_queryStatus == DicomStatus.Pending)
      {
        Thread.Sleep(250);
        if (!((DateTime.UtcNow - startTime).TotalSeconds > timeoutSeconds)) continue;
        if (_cancellationTokenSource.Token.CanBeCanceled)
        {
          _cancellationTokenSource.Cancel();
        }
        break;
      }

      return _queryStatus == DicomStatus.Success;
    }

    public async Task QueryAsync()
    {
      try
      {
        _findResponses.Clear();
        _queryStatus = DicomStatus.Pending;

        var client = CreateClient();
        var cfind = new DicomCFindRequest(DicomUID.ModalityWorklistInformationModelFind)
        {
          Dataset = BuildQueryDataset()
        };

        cfind.OnResponseReceived += OnFindResponseReceived;

        Log.Information("Querying worklist at {DicomScpHost}:{DicomScpPort}", _config.Dicom.ScpHost, _config.Dicom.ScpPort);

        // Set up client event handlers before sending the request
        client.OnCStoreRequest = (request) => Task.FromResult(new DicomCStoreResponse(request, DicomStatus.Success));

        // Add and send the request
        await client.AddRequestAsync(cfind);
        await client.SendAsync(_cancellationTokenSource.Token, DicomClientCancellationMode.ImmediatelyAbortAssociation);

        // Wait for query completion
        bool success = WaitForQueryCompletion();

        if (success && _findResponses.Count > 0)
        {
          Log.Information("Found {FindResponsesCount} orders to process", _findResponses.Count);
          foreach (var dataset in _findResponses)
          {
            string studyInstanceUid = dataset.GetString(DicomTag.StudyInstanceUID);
            if (CacheManager.IsAlreadySent(studyInstanceUid, _config.Cache.Folder))
            {
              Log.Information("Skipping already sent order: {StudyInstanceUid}", studyInstanceUid);
              continue;
            }

            Log.Information("Processing order: {StudyInstanceUid}", studyInstanceUid);
            string ormMessage = ORMGenerator.ReplacePlaceholders(_ormTemplate, dataset);
            if (HL7Sender.SendOrm(_config, ormMessage, _config.HL7.ReceiverHost, _config.HL7.ReceiverPort, _config.Dicom.ScuAeTitle))
            {
              CacheManager.SaveToCache(studyInstanceUid, ormMessage, CacheManager.CacheFolder);
              // If this was a retry, remove from pending queue
              if (RetryManager.IsPendingRetry(studyInstanceUid, CacheManager.CacheFolder))
              {
                RetryManager.RemovePendingMessage(studyInstanceUid, CacheManager.CacheFolder);
                Log.Information("Successfully delivered previously failed message: {StudyInstanceUid}", studyInstanceUid);
              }
            }
            else
            {
              // Save to pending queue for retry
              int attemptCount = 1;
              if (RetryManager.IsPendingRetry(studyInstanceUid, CacheManager.CacheFolder))
              {
                attemptCount = RetryManager.GetAttemptCount(studyInstanceUid, CacheManager.CacheFolder) + 1;
              }
              RetryManager.SavePendingMessage(studyInstanceUid, ormMessage, CacheManager.CacheFolder, attemptCount);
            }
          }
        }

        Log.Information("Query completed with {ResponsesCount} results", _findResponses.Count);
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error during query: {ExMessage}", ex.Message);
        _queryStatus = DicomStatus.ProcessingFailure;
      }
    }

    public IEnumerable<DicomDataset> GetQueryResults()
    {
      return _findResponses.AsEnumerable();
    }

    public void ProcessPendingMessages()
    {
      try
      {
        // Calculate cutoff time based on retry interval
        DateTime cutoffTime = DateTime.Now.AddMinutes(-_config.Retry.RetryIntervalMinutes);

        // Get all pending messages that are ready for retry
        var pendingMessages = RetryManager.GetPendingMessages(CacheManager.CacheFolder, cutoffTime);

        if (!pendingMessages.Any())
        {
          return;
        }

        Log.Information("Processing {Count} pending messages for retry", pendingMessages.Count());

        foreach (var pending in pendingMessages)
        {
          Log.Information($"Retrying ORM for study {pending.StudyInstanceUid} (attempt {pending.AttemptCount} of indefinite retries)");

          if (HL7Sender.SendOrm(_config, pending.OrmMessage, _config.HL7.ReceiverHost, _config.HL7.ReceiverPort, _config.Dicom.ScuAeTitle))
          {
            // Success! Save to successful cache and remove from pending
            CacheManager.SaveToCache(pending.StudyInstanceUid, pending.OrmMessage, CacheManager.CacheFolder);
            RetryManager.RemovePendingMessage(pending.StudyInstanceUid, CacheManager.CacheFolder);
            Log.Information($"Successfully delivered previously failed message: {pending.StudyInstanceUid}");
          }
          else
          {
            // Increment attempt count and save back to pending
            RetryManager.SavePendingMessage(pending.StudyInstanceUid, pending.OrmMessage, CacheManager.CacheFolder, pending.AttemptCount + 1);
          }
        }
      }
      catch (Exception ex)
      {
        Log.Error(ex, $"Error processing pending messages");
      }
    }
  }
}
