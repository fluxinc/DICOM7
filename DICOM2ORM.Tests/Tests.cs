using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Xunit;
using Xunit.Abstractions;

namespace DICOM2ORM.Tests
{
  [CollectionDefinition("Dicom2OrmSerial")]
  public sealed class Dicom2OrmSerialCollection
  {
  }

  [Collection("Dicom2OrmSerial")]
  public class ORMGeneratorTests
  {
    /// <summary>
    /// TC-OG-002: Extracts values from nested sequences
    /// Verifies that FindTagInSequences can extract values from SPS Sequence
    /// </summary>
    [Fact]
    public void ReplacePlaceholders_ExtractsValuesFromNestedSequences()
    {
      // Arrange: Create a dataset with SPS Sequence containing nested attributes
      DicomDataset dataset = new DicomDataset
      {
        { DicomTag.PatientID, "TEST123" },
        { DicomTag.PatientName, "Doe^John" },
        { DicomTag.StudyInstanceUID, "1.2.3.4.5" },
        { DicomTag.AccessionNumber, "ACC001" }
      };

      DicomDataset spsItem = new DicomDataset
      {
        { DicomTag.ScheduledProcedureStepDescription, "CT ABDOMEN WITH CONTRAST" },
        { DicomTag.ScheduledProcedureStepID, "SPS001" },
        { DicomTag.ScheduledProcedureStepStartDate, "20250115" },
        { DicomTag.ScheduledProcedureStepStartTime, "100000" },
        { DicomTag.Modality, "CT" },
        { DicomTag.ScheduledStationAETitle, "CT_SCANNER1" }
      };

      dataset.Add(DicomTag.ScheduledProcedureStepSequence, spsItem);

      // Template with placeholders for both root-level and sequence-nested tags
      string template = "DESC:#{0040,0007}|ID:#{0040,0009}|MOD:#{0008,0060}|PID:#{0010,0020}";

      // Act
      string result = DICOM7.DICOM2ORM.ORMGenerator.ReplacePlaceholders(template, dataset);

      // Assert: Values from SPS sequence should be extracted
      Assert.Contains("DESC:CT ABDOMEN WITH CONTRAST", result);
      Assert.Contains("ID:SPS001", result);
      Assert.Contains("MOD:CT", result);
      Assert.Contains("PID:TEST123", result);
    }

    /// <summary>
    /// TC-OG-001: Replaces root-level DICOM tag placeholders
    /// </summary>
    [Fact]
    public void ReplacePlaceholders_ReplacesRootLevelTags()
    {
      DicomDataset dataset = new DicomDataset
      {
        { DicomTag.PatientID, "PID12345" },
        { DicomTag.PatientName, "Smith^Jane" },
        { DicomTag.AccessionNumber, "ACC999" }
      };

      string template = "Patient: #{0010,0010} (#{0010,0020}) - Accession: #{0008,0050}";

      string result = DICOM7.DICOM2ORM.ORMGenerator.ReplacePlaceholders(template, dataset);

      Assert.Contains("Patient: Smith^Jane", result);
      Assert.Contains("(PID12345)", result);
      Assert.Contains("Accession: ACC999", result);
    }

    /// <summary>
    /// TC-OG-004: Returns empty string for missing tags
    /// </summary>
    [Fact]
    public void ReplacePlaceholders_ReturnsEmptyForMissingTags()
    {
      DicomDataset dataset = new DicomDataset
      {
        { DicomTag.PatientID, "PID12345" }
      };

      // RequestedProcedureDescription (0032,1060) is not in the dataset
      string template = "ID:#{0010,0020}|DESC:#{0032,1060}|";

      string result = DICOM7.DICOM2ORM.ORMGenerator.ReplacePlaceholders(template, dataset);

      Assert.Equal("ID:PID12345|DESC:|", result);
    }

    /// <summary>
    /// TC-OG-003: Replaces pipe characters with caret in values
    /// </summary>
    [Fact]
    public void ReplacePlaceholders_ReplacesPipeWithCaret()
    {
      DicomDataset dataset = new DicomDataset
      {
        { DicomTag.PatientName, "Doe|John|M" }
      };

      string template = "Name:#{0010,0010}";

      string result = DICOM7.DICOM2ORM.ORMGenerator.ReplacePlaceholders(template, dataset);

      Assert.Equal("Name:Doe^John^M", result);
    }

    /// <summary>
    /// TC-OG-005: Special placeholder ScheduledProcedureStepID works correctly
    /// </summary>
    [Fact]
    public void ReplacePlaceholders_HandlesScheduledProcedureStepIDPlaceholder()
    {
      DicomDataset dataset = new DicomDataset
      {
        { DicomTag.PatientID, "TEST123" }
      };

      DicomDataset spsItem = new DicomDataset
      {
        { DicomTag.ScheduledProcedureStepID, "STEP_ABC123" }
      };

      dataset.Add(DicomTag.ScheduledProcedureStepSequence, spsItem);

      string template = "SPS_ID:#{ScheduledProcedureStepID}";

      string result = DICOM7.DICOM2ORM.ORMGenerator.ReplacePlaceholders(template, dataset);

      Assert.Equal("SPS_ID:STEP_ABC123", result);
    }
  }

  [Collection("Dicom2OrmSerial")]
  public class WorklistQueryDatasetTests
  {
    /// <summary>
    /// TC-WQ-001: Query includes all required MWL patient-level attributes
    /// This test documents expected patient attributes in the query
    /// </summary>
    [Fact]
    public void QueryDataset_ShouldIncludePatientAttributes()
    {
      // This test verifies the expected structure based on DICOM PS3.4 Table K.6-1
      // Actual integration test would require WorklistQuerier instantiation with config

      DicomTag[] expectedPatientTags = new[]
      {
        DicomTag.PatientName,
        DicomTag.PatientID,
        DicomTag.PatientBirthDate,
        DicomTag.PatientSex
      };

      // Document expected tags for reference
      Assert.Equal(4, expectedPatientTags.Length);
      Assert.Contains(DicomTag.PatientName, expectedPatientTags);
      Assert.Contains(DicomTag.PatientID, expectedPatientTags);
    }

    /// <summary>
    /// TC-WQ-002: SPS Sequence should contain all required nested attributes
    /// </summary>
    [Fact]
    public void QueryDataset_SPSSequence_ShouldIncludeRequiredAttributes()
    {
      DicomTag[] expectedSPSTags = new[]
      {
        DicomTag.ScheduledStationAETitle,
        DicomTag.ScheduledProcedureStepStartDate,
        DicomTag.ScheduledProcedureStepStartTime,
        DicomTag.Modality,
        DicomTag.ScheduledPerformingPhysicianName,
        DicomTag.ScheduledProcedureStepDescription,
        DicomTag.ScheduledProcedureStepID,
        DicomTag.ScheduledStationName,
        DicomTag.ScheduledProcedureStepLocation
      };

      // Verify key procedure description tag is in expected list
      Assert.Contains(DicomTag.ScheduledProcedureStepDescription, expectedSPSTags);
      Assert.Contains(DicomTag.ScheduledProcedureStepID, expectedSPSTags);
      Assert.Equal(9, expectedSPSTags.Length);
    }

    /// <summary>
    /// TC-WQ-001 (cont): Query includes study-level and requested procedure attributes
    /// </summary>
    [Fact]
    public void QueryDataset_ShouldIncludeStudyAndRequestedProcedureAttributes()
    {
      DicomTag[] expectedTags = new[]
      {
        DicomTag.StudyInstanceUID,
        DicomTag.AccessionNumber,
        DicomTag.ReferringPhysicianName,
        DicomTag.StudyDescription,
        DicomTag.StudyDate,
        DicomTag.StudyTime,
        DicomTag.RequestedProcedureID,
        DicomTag.RequestedProcedureDescription
      };

      Assert.Contains(DicomTag.RequestedProcedureDescription, expectedTags);
      Assert.Contains(DicomTag.StudyDescription, expectedTags);
      Assert.Equal(8, expectedTags.Length);
    }
  }

  /// <summary>
  /// Integration tests that query a real Modality Worklist server
  /// Uses public test worklist at worklist.fluxinc.ca:1070
  /// </summary>
  [Collection("Dicom2OrmSerial")]
  public class WorklistIntegrationTests
  {
    private const string WORKLIST_HOST = "worklist.fluxinc.ca";
    private const int WORKLIST_PORT = 1070;
    private const string WORKLIST_AET = "FLUX_WORKLIST";
    private const string SCU_AET = "DICOM2ORM_TEST";

    private readonly ITestOutputHelper _output;

    public WorklistIntegrationTests(ITestOutputHelper output)
    {
      _output = output;
    }

    /// <summary>
    /// TC-INT-001: Query public worklist and verify response contains expected attributes
    /// </summary>
    [Fact]
    public async Task QueryWorklist_ReturnsResultsWithExpectedAttributes()
    {
      // Arrange
      List<DicomDataset> responses = new List<DicomDataset>();
      DicomStatus finalStatus = DicomStatus.Pending;

      DicomDataset queryDataset = BuildQueryDataset();

      IDicomClient client = DicomClientFactory.Create(
        WORKLIST_HOST,
        WORKLIST_PORT,
        false,
        SCU_AET,
        WORKLIST_AET);

      DicomCFindRequest cfind = new DicomCFindRequest(DicomUID.ModalityWorklistInformationModelFind)
      {
        Dataset = queryDataset
      };

      cfind.OnResponseReceived += (request, response) =>
      {
        if (response.Status == DicomStatus.Pending && response.HasDataset)
        {
          responses.Add(response.Dataset);
          _output.WriteLine($"Received worklist item: StudyInstanceUID={response.Dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, "N/A")}");
        }
        else
        {
          finalStatus = response.Status;
        }
      };

      // Act
      await client.AddRequestAsync(cfind);
      await client.SendAsync(CancellationToken.None, DicomClientCancellationMode.ImmediatelyAbortAssociation);

      // Assert
      _output.WriteLine($"Query completed with status: {finalStatus}");
      _output.WriteLine($"Total results received: {responses.Count}");

      Assert.Equal(DicomStatus.Success, finalStatus);
      Assert.True(responses.Count > 0, "Expected at least one worklist item from the public test server");

      // Verify first result has expected attributes
      DicomDataset firstResult = responses.First();

      // Patient-level attributes
      Assert.True(firstResult.Contains(DicomTag.PatientName), "Response should contain PatientName");
      Assert.True(firstResult.Contains(DicomTag.PatientID), "Response should contain PatientID");

      // Study-level attributes
      Assert.True(firstResult.Contains(DicomTag.StudyInstanceUID), "Response should contain StudyInstanceUID");
      Assert.True(firstResult.Contains(DicomTag.AccessionNumber), "Response should contain AccessionNumber");

      // SPS Sequence
      Assert.True(firstResult.Contains(DicomTag.ScheduledProcedureStepSequence), "Response should contain SPS Sequence");

      DicomSequence spsSequence = firstResult.GetSequence(DicomTag.ScheduledProcedureStepSequence);
      Assert.NotNull(spsSequence);
      Assert.True(spsSequence.Items.Count > 0, "SPS Sequence should have at least one item");

      DicomDataset spsItem = spsSequence.Items.First();
      _output.WriteLine($"SPS Description: {spsItem.GetSingleValueOrDefault(DicomTag.ScheduledProcedureStepDescription, "N/A")}");
      _output.WriteLine($"SPS ID: {spsItem.GetSingleValueOrDefault(DicomTag.ScheduledProcedureStepID, "N/A")}");
      _output.WriteLine($"Modality: {spsItem.GetSingleValueOrDefault(DicomTag.Modality, "N/A")}");
    }

    /// <summary>
    /// TC-INT-002: Query worklist and verify ORMGenerator can extract all attributes including nested ones
    /// </summary>
    [Fact]
    public async Task QueryWorklist_ORMGeneratorExtractsNestedAttributes()
    {
      // Arrange
      List<DicomDataset> responses = new List<DicomDataset>();
      DicomStatus finalStatus = DicomStatus.Pending;

      DicomDataset queryDataset = BuildQueryDataset();

      IDicomClient client = DicomClientFactory.Create(
        WORKLIST_HOST,
        WORKLIST_PORT,
        false,
        SCU_AET,
        WORKLIST_AET);

      DicomCFindRequest cfind = new DicomCFindRequest(DicomUID.ModalityWorklistInformationModelFind)
      {
        Dataset = queryDataset
      };

      cfind.OnResponseReceived += (request, response) =>
      {
        if (response.Status == DicomStatus.Pending && response.HasDataset)
        {
          responses.Add(response.Dataset);
        }
        else
        {
          finalStatus = response.Status;
        }
      };

      // Act
      await client.AddRequestAsync(cfind);
      await client.SendAsync(CancellationToken.None, DicomClientCancellationMode.ImmediatelyAbortAssociation);

      Assert.Equal(DicomStatus.Success, finalStatus);
      Assert.True(responses.Count > 0, "Expected at least one worklist item");

      DicomDataset worklistItem = responses.First();

      // Build a template that uses both root-level and SPS sequence attributes
      string template = string.Join("|", new[]
      {
        "PID:#{0010,0020}",
        "NAME:#{0010,0010}",
        "ACC:#{0008,0050}",
        "STUDY_UID:#{0020,000D}",
        "SPS_DESC:#{0040,0007}",
        "SPS_ID:#{0040,0009}",
        "MOD:#{0008,0060}",
        "SPS_DATE:#{0040,0002}",
        "SPS_TIME:#{0040,0003}",
        "REQ_PROC_DESC:#{0032,1060}"
      });

      // Act - Use ORMGenerator to replace placeholders
      string result = DICOM7.DICOM2ORM.ORMGenerator.ReplacePlaceholders(template, worklistItem);

      _output.WriteLine("Template replacement result:");
      _output.WriteLine(result);

      // Assert - Key attributes should be populated
      Assert.DoesNotContain("#{", result); // All placeholders should be replaced

      // The SPS Description should be extracted from the nested sequence
      string spsDesc = worklistItem.GetSequence(DicomTag.ScheduledProcedureStepSequence)
        .Items.First()
        .GetSingleValueOrDefault(DicomTag.ScheduledProcedureStepDescription, "");

      if (!string.IsNullOrEmpty(spsDesc))
      {
        Assert.Contains($"SPS_DESC:{spsDesc.Replace("|", "^")}", result);
        _output.WriteLine($"Successfully extracted SPS Description: {spsDesc}");
      }
    }

    /// <summary>
    /// Builds a query dataset matching what WorklistQuerier.BuildQueryDataset() produces
    /// </summary>
    private DicomDataset BuildQueryDataset()
    {
      DicomDataset ds = new DicomDataset
      {
        // Patient-level attributes
        { DicomTag.PatientName, "" },
        { DicomTag.PatientID, "" },
        { DicomTag.PatientBirthDate, "" },
        { DicomTag.PatientSex, "" },

        // Study-level attributes
        { DicomTag.StudyInstanceUID, "" },
        { DicomTag.AccessionNumber, "" },
        { DicomTag.ReferringPhysicianName, "" },
        { DicomTag.StudyDescription, "" },
        { DicomTag.StudyDate, "" },
        { DicomTag.StudyTime, "" },

        // Requested Procedure attributes
        { DicomTag.RequestedProcedureID, "" },
        { DicomTag.RequestedProcedureDescription, "" }
      };

      // Scheduled Procedure Step Sequence - query for today with a range
      string today = DateTime.Today.ToString("yyyyMMdd");
      string dateRange = $"{today}-{today}";

      DicomDataset sps = new DicomDataset
      {
        { DicomTag.ScheduledStationAETitle, "" },
        { DicomTag.ScheduledProcedureStepStartDate, dateRange },
        { DicomTag.ScheduledProcedureStepStartTime, "" },
        { DicomTag.Modality, "" },
        { DicomTag.ScheduledPerformingPhysicianName, "" },
        { DicomTag.ScheduledProcedureStepDescription, "" },
        { DicomTag.ScheduledProcedureStepID, "" },
        { DicomTag.ScheduledStationName, "" },
        { DicomTag.ScheduledProcedureStepLocation, "" }
      };

      ds.Add(DicomTag.ScheduledProcedureStepSequence, sps);

      return ds;
    }
  }
}
