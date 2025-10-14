using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Efferent.HL7.V2;
using FellowOakDicom;
using Serilog;

namespace DICOM7.ORU2DICOM
{
  /// <summary>
  /// Represents a cached HL7 ORU message and exposes helpers to convert it to a DICOM dataset
  /// </summary>
  public class CachedORU
  {
    private static readonly ILogger Logger = Log.ForContext<CachedORU>();

    private readonly Message _message;
    private readonly Segment _pidSegment;
    private readonly Segment _pv1Segment;
    private readonly Segment _obrSegment;
    private readonly Segment[] _obxSegments;

    public class PdfAttachment
    {
      public byte[] Data { get; set; }
      public string Title { get; set; }
    }

    public string UUID { get; private set; }
    public string Text { get; }
    public string MessageControlId { get; }
    public string PatientId { get; }
    public string PatientName { get; }
    public string AccessionNumber { get; }
    public string PlacerOrderNumber { get; }
    public string StudyDescription { get; }
    public string ResultStatus { get; }
    public DateTime? ObservationDateTime { get; }

    public CachedORU(string hl7Data, string explicitUuid = null)
    {
      if (string.IsNullOrWhiteSpace(hl7Data))
      {
        throw new ArgumentNullException(nameof(hl7Data));
      }

      Text = hl7Data;

      try
      {
        _message = new Message(hl7Data);
        _message.ParseMessage();
      }
      catch (Exception ex)
      {
        Logger.Error(ex, "Failed to parse incoming ORU message");
        throw new InvalidOperationException("Unable to parse ORU message", ex);
      }

      _pidSegment = _message.Segments("PID").FirstOrDefault();
      _pv1Segment = _message.Segments("PV1").FirstOrDefault();
      _obrSegment = _message.Segments("OBR").FirstOrDefault();
      _obxSegments = _message.Segments("OBX").ToArray();

      Segment msh = _message.Segments("MSH").FirstOrDefault();
      MessageControlId = msh != null ? GetFieldValue(msh, 10) : string.Empty;

      PatientId = GetFieldComponentValue(_pidSegment, 3, 1);
      PatientName = GetFieldComponentsAsPN(_pidSegment, 5);
      AccessionNumber = GetFieldComponentValue(_obrSegment, 3, 1);
      if (string.IsNullOrEmpty(AccessionNumber))
      {
        AccessionNumber = GetFieldComponentValue(_obrSegment, 2, 1);
      }

      PlacerOrderNumber = GetFieldComponentValue(_obrSegment, 2, 1);
      StudyDescription = GetFieldComponentValue(_obrSegment, 4, 2);
      ResultStatus = GetFieldValue(_obrSegment, 25);

      string observationDateRaw = GetFieldValue(_obrSegment, 7);
      ObservationDateTime = TryParseHl7DateTime(observationDateRaw);

      UUID = BuildUuid(MessageControlId, hl7Data);

      if (!string.IsNullOrEmpty(explicitUuid))
      {
        UUID = explicitUuid;
      }
    }

    /// <summary>
    /// Converts the cached message to a DICOM dataset (Basic Text SR) using the supplied fallback description
    /// </summary>
    public DicomDataset AsDicomDataset(string fallbackStudyDescription)
    {
      DicomDataset dataset = CreateBaseDataset("sr", fallbackStudyDescription, DicomUID.BasicTextSRStorage, "SR", 1);

      DicomDataset spsDataset = new DicomDataset();
      if (_pv1Segment != null)
      {
        string scheduledPhysician = GetFieldComponentsAsXCN(_pv1Segment, 7);
        if (!string.IsNullOrEmpty(scheduledPhysician))
        {
          spsDataset.AddOrUpdate(DicomTag.ScheduledPerformingPhysicianName, TruncateForVR(scheduledPhysician, DicomTag.ScheduledPerformingPhysicianName));
        }
      }

      if (_obrSegment != null)
      {
        string observationDateTime = GetFieldValue(_obrSegment, 7);
        if (!string.IsNullOrEmpty(observationDateTime) && observationDateTime.Length >= 8)
        {
          spsDataset.AddOrUpdate(DicomTag.ScheduledProcedureStepStartDate, observationDateTime.Substring(0, 8));
        }

        string procedureDesc = GetFieldValue(_obrSegment, 4);
        if (!string.IsNullOrEmpty(procedureDesc))
        {
          spsDataset.AddOrUpdate(DicomTag.ScheduledProcedureStepDescription, TruncateForVR(procedureDesc, DicomTag.ScheduledProcedureStepDescription));
        }
      }

      dataset.AddOrUpdate(new DicomSequence(DicomTag.ScheduledProcedureStepSequence, spsDataset));

      List<DicomDataset> contentItems = new List<DicomDataset>();

      string summaryText = BuildObservationNarrative();
      DicomDataset summaryConcept = new DicomDataset
      {
        { DicomTag.CodeValue, "ORU_SUMMARY" },
        { DicomTag.CodingSchemeDesignator, "99FLUX" },
        { DicomTag.CodeMeaning, "HL7 ORU Summary" }
      };

      DicomDataset summaryItem = new DicomDataset
      {
        { DicomTag.RelationshipType, "CONTAINS" },
        { DicomTag.ValueType, "TEXT" }
      };
      summaryItem.Add(new DicomSequence(DicomTag.ConceptNameCodeSequence, summaryConcept));
      summaryItem.AddOrUpdate(DicomTag.TextValue, summaryText);
      contentItems.Add(summaryItem);

      string rawMessage = NormalizeMultiline(Text);
      if (!string.IsNullOrEmpty(rawMessage) && rawMessage.Length < 32000)
      {
        DicomDataset rawConcept = new DicomDataset
        {
          { DicomTag.CodeValue, "ORU_RAW" },
          { DicomTag.CodingSchemeDesignator, "99FLUX" },
          { DicomTag.CodeMeaning, "Raw HL7 Message" }
        };

        DicomDataset rawItem = new DicomDataset
        {
          { DicomTag.RelationshipType, "CONTAINS" },
          { DicomTag.ValueType, "TEXT" }
        };
        rawItem.Add(new DicomSequence(DicomTag.ConceptNameCodeSequence, rawConcept));
        rawItem.AddOrUpdate(DicomTag.TextValue, rawMessage);
        contentItems.Add(rawItem);
      }

      dataset.AddOrUpdate(new DicomSequence(DicomTag.ContentSequence, contentItems.ToArray()));

      return dataset;
    }

    public bool TryGetPdfAttachment(out PdfAttachment attachment)
    {
      attachment = null;

      if (_obxSegments == null || _obxSegments.Length == 0)
      {
        return false;
      }

      foreach (Segment obx in _obxSegments)
      {
        string valueType = GetFieldValue(obx, 2);
        if (!string.Equals(valueType, "ED", StringComparison.OrdinalIgnoreCase))
        {
          continue;
        }

        string encoding = GetFieldComponentValue(obx, 5, 4);
        string data = GetFieldComponentValue(obx, 5, 5);

        if (string.IsNullOrEmpty(data) || !IsBase64EncodingIndicator(encoding))
        {
          continue;
        }

        string dataSubtype = GetFieldComponentValue(obx, 5, 3);
        string typeOfData = GetFieldComponentValue(obx, 5, 2);
        if (!ContainsPdfIndicator(dataSubtype) && !ContainsPdfIndicator(typeOfData))
        {
          continue;
        }

        try
        {
          byte[] pdfBytes = Convert.FromBase64String(data);
          if (pdfBytes == null || pdfBytes.Length == 0)
          {
            continue;
          }

          string documentTitle = GetFieldComponentValue(obx, 3, 2);
          if (string.IsNullOrWhiteSpace(documentTitle))
          {
            documentTitle = GetFieldComponentValue(obx, 3, 1);
          }

          attachment = new PdfAttachment
          {
            Data = pdfBytes,
            Title = documentTitle
          };

          return true;
        }
        catch (FormatException ex)
        {
          Logger.Warning(ex, "Invalid Base64 PDF payload in ORU message {UUID}", UUID);
        }
      }

      return false;
    }

    public DicomDataset BuildEncapsulatedPdfDataset(PdfAttachment attachment, string fallbackStudyDescription)
    {
      if (attachment == null)
      {
        throw new ArgumentNullException(nameof(attachment));
      }

      if (attachment.Data == null || attachment.Data.Length == 0)
      {
        throw new ArgumentException("PDF attachment data is empty", nameof(attachment));
      }

      DicomDataset dataset = CreateBaseDataset("pdf", fallbackStudyDescription, DicomUID.EncapsulatedPDFStorage, "OT", 2);

      string documentTitle = string.IsNullOrWhiteSpace(attachment.Title)
        ? (string.IsNullOrWhiteSpace(fallbackStudyDescription) ? "Diagnostic Report" : fallbackStudyDescription)
        : attachment.Title;

      dataset.AddOrUpdate(DicomTag.DocumentTitle, TruncateForVR(documentTitle, DicomTag.DocumentTitle));
      dataset.AddOrUpdate(DicomTag.MIMETypeOfEncapsulatedDocument, "application/pdf");
      dataset.AddOrUpdate(DicomTag.BurnedInAnnotation, "NO");
      dataset.AddOrUpdate(DicomTag.EncapsulatedDocument, attachment.Data);

      return dataset;
    }

    public string SummaryForLog()
    {
      return string.Format("ORU {0} for {1} ({2})", string.IsNullOrEmpty(MessageControlId) ? UUID : MessageControlId, string.IsNullOrEmpty(PatientName) ? PatientId : PatientName, string.IsNullOrEmpty(AccessionNumber) ? "no accession" : AccessionNumber);
    }

    private string SelectStudyInstanceUid()
    {
      string[] candidates =
      {
        GetFieldComponentValue(_obrSegment, 3, 1),
        GetFieldComponentValue(_obrSegment, 2, 1),
        MessageControlId
      };

      foreach (string candidate in candidates)
      {
        if (string.IsNullOrEmpty(candidate))
        {
          continue;
        }

        string normalized = NormalizeUidCandidate(candidate);
        if (IsValidUid(normalized))
        {
          return normalized;
        }
      }

      return GenerateDeterministicUid("study");
    }

    private DicomDataset CreateBaseDataset(string contextKey, string fallbackStudyDescription, DicomUID sopClassUid, string modality, int seriesNumber)
    {
      string studyInstanceUid = SelectStudyInstanceUid();
      string seriesInstanceUid = GenerateDeterministicUid(contextKey + "-series");
      string sopInstanceUid = GenerateDeterministicUid(contextKey + "-sop");

      DicomDataset dataset = new DicomDataset
      {
        { DicomTag.SpecificCharacterSet, "ISO_IR 192" },
        { DicomTag.SOPClassUID, sopClassUid },
        { DicomTag.SOPInstanceUID, sopInstanceUid },
        { DicomTag.StudyInstanceUID, studyInstanceUid },
        { DicomTag.SeriesInstanceUID, seriesInstanceUid },
        { DicomTag.Modality, modality },
        { DicomTag.SeriesNumber, seriesNumber },
        { DicomTag.InstanceNumber, 1 }
      };

      dataset.AddOrUpdate(DicomTag.PatientID, TruncateForVR(string.IsNullOrEmpty(PatientId) ? UUID : PatientId, DicomTag.PatientID));
      dataset.AddOrUpdate(DicomTag.PatientName, TruncateForVR(string.IsNullOrEmpty(PatientName) ? "UNKNOWN" : PatientName, DicomTag.PatientName));

      string birthDate = GetFieldValue(_pidSegment, 7);
      if (!string.IsNullOrEmpty(birthDate) && birthDate.Length >= 8)
      {
        dataset.AddOrUpdate(DicomTag.PatientBirthDate, birthDate.Substring(0, 8));
      }

      string patientSex = GetFieldValue(_pidSegment, 8);
      if (!string.IsNullOrEmpty(patientSex))
      {
        dataset.AddOrUpdate(DicomTag.PatientSex, TruncateForVR(patientSex, DicomTag.PatientSex));
      }

      string patientAddress = GetFieldValue(_pidSegment, 11);
      if (!string.IsNullOrEmpty(patientAddress))
      {
        dataset.AddOrUpdate(DicomTag.PatientAddress, TruncateForVR(patientAddress, DicomTag.PatientAddress));
      }

      if (_pv1Segment != null)
      {
        string referringPhysician = GetFieldComponentsAsXCN(_pv1Segment, 8);
        if (!string.IsNullOrEmpty(referringPhysician))
        {
          dataset.AddOrUpdate(DicomTag.ReferringPhysicianName, TruncateForVR(referringPhysician, DicomTag.ReferringPhysicianName));
        }

        string institutionName = GetFieldComponentValue(_pv1Segment, 3, 4);
        if (!string.IsNullOrEmpty(institutionName))
        {
          dataset.AddOrUpdate(DicomTag.InstitutionName, TruncateForVR(institutionName, DicomTag.InstitutionName));
        }
      }

      string performingPhysician = GetFieldComponentsAsXCN(_obrSegment, 16);
      if (string.IsNullOrEmpty(performingPhysician))
      {
        performingPhysician = GetFieldComponentsAsXCN(_pv1Segment, 7);
      }

      if (!string.IsNullOrEmpty(performingPhysician))
      {
        dataset.AddOrUpdate(DicomTag.PerformingPhysicianName, TruncateForVR(performingPhysician, DicomTag.PerformingPhysicianName));
      }

      if (!string.IsNullOrEmpty(AccessionNumber))
      {
        dataset.AddOrUpdate(DicomTag.AccessionNumber, TruncateForVR(AccessionNumber, DicomTag.AccessionNumber));
      }

      if (!string.IsNullOrEmpty(PlacerOrderNumber))
      {
        dataset.AddOrUpdate(DicomTag.StudyID, TruncateForVR(PlacerOrderNumber, DicomTag.StudyID));
      }

      string studyDescription = !string.IsNullOrEmpty(StudyDescription) ? StudyDescription : fallbackStudyDescription;
      if (!string.IsNullOrEmpty(studyDescription))
      {
        dataset.AddOrUpdate(DicomTag.StudyDescription, TruncateForVR(studyDescription, DicomTag.StudyDescription));
        dataset.AddOrUpdate(DicomTag.SeriesDescription, TruncateForVR(studyDescription, DicomTag.SeriesDescription));
      }

      dataset.AddOrUpdate(DicomTag.Manufacturer, "Flux Inc");

      DateTime observationTime = ObservationDateTime ?? DateTime.Now;
      dataset.AddOrUpdate(DicomTag.StudyDate, observationTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
      dataset.AddOrUpdate(DicomTag.StudyTime, observationTime.ToString("HHmmss", CultureInfo.InvariantCulture));
      dataset.AddOrUpdate(DicomTag.ContentDate, observationTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
      dataset.AddOrUpdate(DicomTag.ContentTime, observationTime.ToString("HHmmss", CultureInfo.InvariantCulture));
      dataset.AddOrUpdate(DicomTag.InstanceCreationDate, DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
      dataset.AddOrUpdate(DicomTag.InstanceCreationTime, DateTime.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture));

      return dataset;
    }

    private string GenerateDeterministicUid(string context)
    {
      using (SHA256 sha = SHA256.Create())
      {
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(UUID + context));
        byte[] guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);
        Guid guid = new Guid(guidBytes);
        return GuidToDicomUid(guid);
      }
    }

    private string BuildObservationNarrative()
    {
      if (_obxSegments == null || _obxSegments.Length == 0)
      {
        return NormalizeMultiline(Text);
      }

      StringBuilder builder = new StringBuilder();
      int index = 1;

      foreach (Segment obx in _obxSegments)
      {
        string label = GetFieldComponentValue(obx, 3, 2);
        if (string.IsNullOrEmpty(label))
        {
          label = GetFieldComponentValue(obx, 3, 1);
        }

        string value = GetFieldValue(obx, 5);
        if (string.IsNullOrEmpty(value))
        {
          continue;
        }

        string units = GetFieldComponentValue(obx, 6, 1);
        string referenceRange = GetFieldValue(obx, 7);
        string status = GetFieldValue(obx, 11);

        builder.Append(index).Append(". ");
        builder.Append(string.IsNullOrEmpty(label) ? "Observation" : label).Append(": ");
        builder.Append(value);

        if (!string.IsNullOrEmpty(units))
        {
          builder.Append(" ").Append(units);
        }

        if (!string.IsNullOrEmpty(referenceRange))
        {
          builder.Append(" (ref ").Append(referenceRange).Append(")");
        }

        if (!string.IsNullOrEmpty(status))
        {
          builder.Append(" [status ").Append(status).Append("]");
        }

        builder.AppendLine();
        index++;
      }

      string summary = builder.ToString().Trim();
      return string.IsNullOrEmpty(summary) ? NormalizeMultiline(Text) : summary;
    }

    private static string NormalizeMultiline(string value)
    {
      return string.IsNullOrEmpty(value) ? string.Empty : value.Replace('\r', '\n');
    }

    private static string GetFieldValue(Segment segment, int fieldIndex)
    {
      if (segment == null || fieldIndex < 1)
      {
        return string.Empty;
      }

      try
      {
        Field field = segment.Fields(fieldIndex);
        return field != null ? field.Value ?? string.Empty : string.Empty;
      }
      catch
      {
        return string.Empty;
      }
    }

    private static string GetFieldComponentValue(Segment segment, int fieldIndex, int componentIndex)
    {
      if (segment == null || fieldIndex < 1 || componentIndex < 1)
      {
        return string.Empty;
      }

      try
      {
        Field field = segment.Fields(fieldIndex);
        if (field == null)
        {
          return string.Empty;
        }

        IList<Component> components = field.Components();
        if (components == null || componentIndex > components.Count)
        {
          return string.Empty;
        }

        return components[componentIndex - 1].Value ?? string.Empty;
      }
      catch
      {
        return string.Empty;
      }
    }

    private static string GetFieldComponentsAsPN(Segment segment, int fieldIndex)
    {
      if (segment == null)
      {
        return string.Empty;
      }

      string familyName = GetFieldComponentValue(segment, fieldIndex, 1);
      string givenName = GetFieldComponentValue(segment, fieldIndex, 2);
      string middleName = GetFieldComponentValue(segment, fieldIndex, 3);
      string suffix = GetFieldComponentValue(segment, fieldIndex, 4);
      string prefix = GetFieldComponentValue(segment, fieldIndex, 5);

      List<string> components = new List<string>();
      if (!string.IsNullOrEmpty(familyName)) components.Add(familyName);
      if (!string.IsNullOrEmpty(givenName)) components.Add(givenName);
      if (!string.IsNullOrEmpty(middleName)) components.Add(middleName);
      if (!string.IsNullOrEmpty(prefix)) components.Add(prefix);
      if (!string.IsNullOrEmpty(suffix)) components.Add(suffix);

      return string.Join("^", components);
    }

    private static string GetFieldComponentsAsXCN(Segment segment, int fieldIndex)
    {
      if (segment == null)
      {
        return string.Empty;
      }

      string familyName = GetFieldComponentValue(segment, fieldIndex, 2);
      string givenName = GetFieldComponentValue(segment, fieldIndex, 3);
      string middleName = GetFieldComponentValue(segment, fieldIndex, 4);
      string suffix = GetFieldComponentValue(segment, fieldIndex, 5);
      string prefix = GetFieldComponentValue(segment, fieldIndex, 6);

      List<string> components = new List<string>();
      if (!string.IsNullOrEmpty(familyName)) components.Add(familyName);
      if (!string.IsNullOrEmpty(givenName)) components.Add(givenName);
      if (!string.IsNullOrEmpty(middleName)) components.Add(middleName);
      if (!string.IsNullOrEmpty(prefix)) components.Add(prefix);
      if (!string.IsNullOrEmpty(suffix)) components.Add(suffix);

      return string.Join("^", components);
    }

    private static bool IsValidUid(string candidate)
    {
      if (string.IsNullOrEmpty(candidate))
      {
        return false;
      }

      if (candidate.Length > 64)
      {
        return false;
      }

      foreach (char c in candidate)
      {
        if (c != '.' && !char.IsDigit(c))
        {
          return false;
        }
      }

      return char.IsDigit(candidate[0]);
    }

    private static string NormalizeUidCandidate(string value)
    {
      if (string.IsNullOrWhiteSpace(value))
      {
        return string.Empty;
      }

      StringBuilder builder = new StringBuilder();
      foreach (char c in value)
      {
        if (c == '.' || char.IsDigit(c))
        {
          builder.Append(c);
        }
      }

      return builder.ToString();
    }

    private static bool ContainsPdfIndicator(string value)
    {
      return !string.IsNullOrEmpty(value) && value.IndexOf("pdf", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsBase64EncodingIndicator(string value)
    {
      if (string.IsNullOrEmpty(value))
      {
        return false;
      }

      return string.Equals(value, "Base64", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "B64", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime? TryParseHl7DateTime(string dateValue)
    {
      if (string.IsNullOrEmpty(dateValue))
      {
        return null;
      }

      string[] formats =
      {
        "yyyyMMddHHmmss",
        "yyyyMMddHHmm",
        "yyyyMMddHH",
        "yyyyMMdd",
        "yyyyMMddHHmmsszzz",
        "yyyyMMddHHmmzzz"
      };

      foreach (string format in formats)
      {
        DateTime parsed;
        if (DateTime.TryParseExact(dateValue, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed))
        {
          return parsed;
        }
      }

      return null;
    }

    private static string BuildUuid(string messageControlId, string content)
    {
      string candidate = SanitizeIdentifier(messageControlId);
      if (!string.IsNullOrEmpty(candidate))
      {
        return candidate;
      }

      using (SHA256 sha = SHA256.Create())
      {
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(content));
        return BitConverter.ToString(hash).Replace("-", string.Empty).Substring(0, 32);
      }
    }

    private static string SanitizeIdentifier(string value)
    {
      if (string.IsNullOrWhiteSpace(value))
      {
        return string.Empty;
      }

      StringBuilder builder = new StringBuilder(value.Length);
      foreach (char c in value)
      {
        if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
        {
          builder.Append(c);
        }
      }

      return builder.ToString();
    }

    private static string TruncateForVR(string value, DicomTag tag)
    {
      if (string.IsNullOrEmpty(value))
      {
        return value;
      }

      DicomVR vr = tag.DictionaryEntry.ValueRepresentations.First();
      int maxLength;

      switch (vr.Code)
      {
        case "AE":
          maxLength = 16;
          break;
        case "CS":
          maxLength = 16;
          break;
        case "DS":
          maxLength = 16;
          break;
        case "LO":
          maxLength = 64;
          break;
        case "PN":
          maxLength = 64;
          break;
        case "SH":
          maxLength = 16;
          break;
        case "ST":
          maxLength = 1024;
          break;
        case "UT":
          maxLength = int.MaxValue;
          break;
        default:
          return value;
      }

      if (value.Length <= maxLength)
      {
        return value;
      }

      Logger.Warning("Truncating value for tag {Tag} from {OriginalLength} to {MaxLength} characters", tag.DictionaryEntry.Name, value.Length, maxLength);
      return value.Substring(0, maxLength);
    }

    private static string GuidToDicomUid(Guid guid)
    {
      byte[] bytes = guid.ToByteArray();
      byte[] unsignedBytes = new byte[bytes.Length + 1];
      Array.Copy(bytes, unsignedBytes, bytes.Length);
      BigInteger value = new BigInteger(unsignedBytes);
      if (value < 0)
      {
        value = BigInteger.Negate(value);
      }

      return "2.25." + value.ToString();
    }
  }
}
