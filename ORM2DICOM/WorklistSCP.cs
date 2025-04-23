using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DICOM7.Shared;
using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.Logging;

namespace DICOM7.ORM2DICOM
{
  internal class WorklistSCP : BasicSCP, IDicomCFindProvider
  {
    public WorklistSCP(
      INetworkStream stream,
      Encoding fallBackEncoding,
      ILogger log,
      DicomServiceDependencies dependencies,
      string aeTitle) : base(stream, fallBackEncoding, log, dependencies, aeTitle)
    {

    }

    public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest dicomRequest)
    {
      if (dicomRequest is null)
        throw new ArgumentNullException(nameof(dicomRequest));

      _logger.LogInformation("C-Find request received from {CallingAE}", LastAssociatedAeTitle);

      if (dicomRequest.Level != DicomQueryRetrieveLevel.NotApplicable)
      {
        _logger.LogInformation("C-Find Q/R level not supported. Rejecting request from {CallingAE}",
              LastAssociatedAeTitle);
          yield return new DicomCFindResponse(dicomRequest, DicomStatus.QueryRetrieveUnableToProcess);
      }
      else
      {
          // Get active ORMs from cache
          IEnumerable<DicomDataset> ormDatasets = GetWorklistItemsFromActiveORMs(dicomRequest);

          foreach (DicomDataset result in ormDatasets)
          {
              yield return new DicomCFindResponse(dicomRequest, DicomStatus.Pending) { Dataset = result };
          }

          yield return new DicomCFindResponse(dicomRequest, DicomStatus.Success);
      }
    }

    protected override void HandlePresentContexts(DicomAssociation association)
    {
      foreach (DicomPresentationContext pc in association.PresentationContexts)
      {
        if (pc.AbstractSyntax == DicomUID.Verification ||
            pc.AbstractSyntax == DicomUID.ModalityWorklistInformationModelFind)
        {
          pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes);
        }
        else
        {
          _logger.LogWarning("Requested abstract syntax {AbstractSyntax} from {CallingAE} not supported",
            pc.AbstractSyntax, association.CallingAE);
          pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
        }
      }
    }

    private IEnumerable<DicomDataset> GetWorklistItemsFromActiveORMs(DicomCFindRequest request)
    {
      // Get all active ORM messages
      IEnumerable<CachedORM> orms = CachedORM.GetActiveORMs();

      // Convert each ORM to a DICOM dataset
      List<DicomDataset> datasets = orms.Select(orm => orm.AsDicomDataset())
                         .Where(dataset => dataset != null)
                         .ToList();

      // Log the number of datasets
      _logger.LogInformation("Found {Count} active ORM messages for worklist", datasets.Count);

      // Filter the datasets based on the request
      return FilterWorklistItems(request.Dataset, datasets);
    }

    private IEnumerable<DicomDataset> FilterWorklistItems(DicomDataset requestDataset, IEnumerable<DicomDataset> datasets)
    {
      List<DicomDataset> filteredDatasets = new List<DicomDataset>();

      foreach (DicomDataset dataset in datasets)
      {
        bool isMatch = true;

        // Match against all attributes in the request
        foreach (DicomItem element in requestDataset)
        {
          // Skip sequence elements for simplicity
          if (element.ValueRepresentation == DicomVR.SQ)
            continue;

          // Skip empty elements
          if (!requestDataset.Contains(element.Tag) || string.IsNullOrEmpty(requestDataset.GetString(element.Tag)))
            continue;

          // If the request has this tag and the dataset doesn't match, exclude it
          if (dataset.Contains(element.Tag))
          {
            string requestValue = requestDataset.GetString(element.Tag);
            string datasetValue = dataset.GetString(element.Tag);

            // Handle wildcard matching
            if (!string.IsNullOrEmpty(requestValue) && requestValue != "*")
            {
              if (requestValue.Contains('*'))
              {
                // Simple wildcard matching
                string pattern = requestValue.Replace("*", "");
                if (!string.IsNullOrEmpty(pattern) && !datasetValue.Contains(pattern))
                {
                  isMatch = false;
                  break;
                }
              }
              else if (requestValue != datasetValue)
              {
                isMatch = false;
                break;
              }
            }
          }
        }

        if (isMatch)
        {
          filteredDatasets.Add(dataset);
        }
      }

      return filteredDatasets;
    }
  }
}
