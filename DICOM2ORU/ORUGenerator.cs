using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Efferent.HL7.V2;
using FellowOakDicom;
using Serilog;

namespace DICOM7.DICOM2ORU;

/// <summary>
///   Handles the generation and customization of HL7 ORU messages from DICOM datasets
/// </summary>
public class ORUGenerator
{
  /// <summary>
  ///   Loads an ORU template from the specified file path or returns a default template if not found
  /// </summary>
  /// <param name="path">The file path where the ORU template is located</param>
  /// <returns>The loaded template string or a default template if file not found</returns>
  public static string LoadTemplate(string path)
  {
    // Make sure the path is properly normalized for the current OS
    string normalizedPath = Path.GetFullPath(path);
    if (!File.Exists(normalizedPath))
    {
      Log.Information("Saving default v23 ORU template to '{Path}'", normalizedPath);
      File.WriteAllText(normalizedPath, GetDefaultV23OruMessage().SerializeMessage());
    }

    Log.Information("Reading ORU template from '{Path}'", normalizedPath);

    // Read the template as plain text instead of attempting to parse it as an HL7 message
    // This allows templates with placeholders to be loaded without validation errors
    return File.ReadAllText(normalizedPath);
  }

  /// <summary>
  ///   Returns a default HL7 v2.3 ORU message template with DICOM tag placeholders
  /// </summary>
  /// <returns>A string containing the default ORU template</returns>
  private static Message GetDefaultV23OruMessage()
  {
    Message message = new();
    message.AddSegmentMSH(
      "DICOM7_DICOM2ORU",
      "FLUXINC",
      "FLUX^fluxinc.co^DNS",
      "fluxinc^2.16.840.1.113883.3.8873^ISO",
      "",
      "ORU^R01^ORU_R01",
      "#{0020,000D}", // Study Instance UID as message control ID
      "P",
      "2.5.1");

    HL7Encoding enc = new();

    Segment pid = new("PID", enc);
    pid.AddNewField("1"); // PID.1 Set ID
    pid.AddNewField("#{0010,0020}", 2); // PID.2 Patient ID
    pid.AddNewField("#{0010,0020}", 3); // PID.3 Patient ID (Alternate)
    pid.AddNewField("#{0010,0010}", 5); // PID.5 Patient Name
    pid.AddNewField("#{0010,0030}", 7); // PID.7 Date of Birth
    pid.AddNewField("#{0010,0040}", 8); // PID.8 Sex
    message.AddNewSegment(pid);

    Segment pv1 = new("PV1", enc);
    pv1.AddNewField("1"); // PV1.1 Set ID
    pv1.AddNewField("O"); // PV1.2 Patient Class
    pv1.AddNewField("#{0008,0080}", 3); // PV1.3 Assigned Location (Institution Name)
    pv1.AddNewField("#{0008,0050}", 19); // PV1.19 Visit Number (Accession Number)
    pv1.AddNewField("#{CurrentDateTime}", 44); // PV1.44 Admit Date/Time

    message.AddNewSegment(pv1);

    Segment orc = new("ORC", enc);
    orc.AddNewField("RE"); // ORC.1 Order Control (RE = Observations/Results)
    orc.AddNewField("#{0008,0050}"); // ORC.2 Placer Order Number (Accession Number)
    orc.AddNewField("#{0020,000D}"); // ORC.3 Filler Order Number (Study Instance UID)
    orc.AddNewField("CM"); // ORC.4 Order Status (CM = Completed)
    message.AddNewSegment(orc);

    Segment obr = new("OBR", enc);
    obr.AddNewField("1"); // OBR.1 Set ID
    obr.AddNewField("#{0008,0050}", 2); // OBR.2 Placer Order Number (Accession Number)
    obr.AddNewField("#{0020,000D}", 3); // OBR.3 Filler Order Number (Study Instance UID)
    obr.AddNewField("#{0008,1030}", 4); // OBR.4 Universal Service ID (Study Description)
    obr.AddNewField("#{0008,0020}#{0008,0030}", 7); // OBR.7 Observation Date/Time (Study Date + Study Time)
    obr.AddNewField("#{CurrentDateTime}", 8); // OBR.8 Observation End Date/Time
    obr.AddNewField("#{0008,1070}", 16); // OBR.16 Ordering Provider (Operators' Name)
    obr.AddNewField("F", 25); // OBR.25 Result Status (F = Final)

    message.AddNewSegment(obr);

    // Default OBX for report if available
    Segment obx = new("OBX", enc);
    obx.AddNewField("1"); // OBX.1 Set ID
    obx.AddNewField("ED"); // OBX.2 Value Type (ED = Encapsulated Data)
    obx.AddNewField("47045-0^Study report^LN", 3); // OBX.3 Observation Identifier with LOINC code
    obx.AddNewField("1", 4); // OBX.4 Observation Sub-ID

    // Set OBX.5 as a structured field with placeholder for future base64 data
    Field edField = new(enc);
    edField.AddNewComponent(new Component(enc) { Value = "" }); // Component 1: Source App
    edField.AddNewComponent(new Component(enc) { Value = "AP" }); // Component 2: Type of Data
    edField.AddNewComponent(new Component(enc) { Value = "Octet-stream" }); // Component 3: Data Subtype
    edField.AddNewComponent(new Component(enc) { Value = "Base64" }); // Component 4: Encoding
    edField.AddNewComponent(new Component(enc) { Value = "" }); // Component 5: Data (empty initially)
    obx.AddNewField(edField, 5);

    // Set OBX.11 Result Status to Final
    obx.AddNewField("F", 11); // OBX.11 Observation Result Status (F = Final)
    message.AddNewSegment(obx);


    return message;
  }

  /// <summary>
  ///   Replaces DICOM tag placeholders in an ORU template with values from a DICOM dataset
  /// </summary>
  /// <param name="template">The ORU template containing placeholders in the format #{group,element}</param>
  /// <param name="dataset">The DICOM dataset containing values to insert into the template</param>
  /// <returns>A completed HL7 ORU message with placeholders replaced by dataset values</returns>
  public static string ReplacePlaceholders(string template, DicomDataset dataset)
  {
    // First replace special placeholders that aren't direct DICOM tags
    template = ReplaceSpecialPlaceholders(template, dataset);

    // Then replace standard DICOM tag placeholders
    template = ReplaceDicomTagPlaceholders(template, dataset);

    // Finally, process SR-specific template syntax (ForEach loops, If conditions, SR paths)
    if (!IsSRDataset(dataset)) return template;

    try
    {
      SRTemplateProcessor srProcessor = new(dataset);
      template = srProcessor.ProcessTemplate(template);
    }
    catch (Exception ex)
    {
      Log.Error(ex, "Error processing SR template: {Message}", ex.Message);
    }

    return template;
  }

  /// <summary>
  ///   Replaces DICOM tag placeholders in the template
  /// </summary>
  private static string ReplaceDicomTagPlaceholders(string template, DicomDataset dataset)
  {
    return Regex.Replace(template, @"#\{([0-9a-fA-F]{4}),([0-9a-fA-F]{4})\}", match =>
    {
      try
      {
        string groupStr = match.Groups[1].Value;
        string elementStr = match.Groups[2].Value;
        DicomTag tag = new(Convert.ToUInt16(groupStr, 16), Convert.ToUInt16(elementStr, 16));
        // First try to get the value directly from the dataset
        if (dataset.Contains(tag))
          try
          {
            if (tag.DictionaryEntry.ValueRepresentations[0] == DicomVR.SQ)
            {
              // For sequence tags, try to find the first occurrence of this tag in any sequence
              string sequenceValue = FindTagInSequences(dataset, tag);

              return !string.IsNullOrEmpty(sequenceValue) ? sequenceValue.Replace("|", "^") : "[Sequence]";
            }

            try
            {
              string value = dataset.GetSingleValueOrDefault(tag, "");
              return value.Replace("|", "^");
            }
            catch
            {
              DicomElement dicomElement = dataset.GetDicomItem<DicomElement>(tag);
              if (dicomElement != null)
                return dicomElement.ToString().Replace("|", "^");
            }
          }
          catch (Exception ex)
          {
            Log.Warning("Unable to get string value for tag ({GroupStr},{ElementStr}): {Message}",
              groupStr, elementStr, ex.Message);
          }
        else
        {
          // If the tag is not directly in the dataset, search for it in sequences
          string sequenceValue = FindTagInSequences(dataset, tag);
          if (!string.IsNullOrEmpty(sequenceValue))
            return sequenceValue.Replace("|", "^");
        }

        return "";
      }
      catch (Exception ex)
      {
        Log.Error("Error processing tag in template: {Message}", ex.Message);
        return "";
      }
    });
  }

  /// <summary>
  ///   Checks if the dataset is a Structured Report
  /// </summary>
  private static bool IsSRDataset(DicomDataset dataset)
  {
    // Check if it's an SR by examining the SOP Class UID or Modality tag
    bool hasSrSopClass = dataset.Contains(DicomTag.SOPClassUID) &&
                         dataset.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty)
                           .Equals(DicomUID.ComprehensiveSRStorage.UID, StringComparison.OrdinalIgnoreCase);

    bool hasModalitySR = dataset.Contains(DicomTag.Modality) &&
                         dataset.GetSingleValueOrDefault(DicomTag.Modality, string.Empty)
                           .Equals("SR", StringComparison.OrdinalIgnoreCase);

    // Also check if it has a ContentSequence which is characteristic of SRs
    bool hasContentSequence = dataset.Contains(DicomTag.ContentSequence);

    return (hasSrSopClass || hasModalitySR) && hasContentSequence;
  }

  /// <summary>
  ///   Generic method to updates the OBX segment with Base64 encoded data.
  /// </summary>
  /// <param name="messageText">The HL7 message string</param>
  /// <param name="base64Data">Base64 encoded data string</param>
  /// <param name="dataType">HL7 Data Type component (e.g., "Application")</param>
  /// <param name="dataSubtype">HL7 Data Subtype component (e.g., "PDF")</param>
  /// <param name="observationIdentifier">HL7 Observation Identifier (OBX-3, e.g., "SR" or "PDF_DATA")</param>
  /// <returns>Updated HL7 message string</returns>
  private static string UpdateObxWithBase64Data(string messageText, string base64Data, string dataType,
    string dataSubtype, string observationIdentifier)
  {
    if (string.IsNullOrEmpty(base64Data))
    {
      Log.Warning("Base64 data is null or empty, cannot add to OBX segment for {ObservationIdentifier}",
        observationIdentifier);
      return messageText;
    }

    try
    {
      Message message = new(messageText);
      message.ParseMessage();

      HL7Encoding enc = new();
      // Find existing OBX with the appropriate observation identifier (should be in template)
      Segment obxSegment = message.Segments("OBX").FirstOrDefault(seg =>
        seg.Fields(3) != null && seg.Fields(3).Value == observationIdentifier);

      if (obxSegment == null)
      {
        Log.Warning("Could not find an OBX segment with identifier {ObservationIdentifier} in the template",
          observationIdentifier);
        return messageText;
      }

      // Create the ED field for the content
      Field edField = new(enc);

      // HL7 ED Component structure: Source Application^Type of Data^Data Subtype^Encoding^Data
      edField.AddNewComponent(new Component(enc) { Value = "" }); // Source App (Optional)
      edField.AddNewComponent(new Component(enc) { Value = dataType }); // Type of Data
      edField.AddNewComponent(new Component(enc) { Value = dataSubtype }); // Data Subtype
      edField.AddNewComponent(new Component(enc) { Value = "Base64" }); // Encoding
      edField.AddNewComponent(new Component(enc) { Value = base64Data }); // Data

      // Update OBX.5 (Observation Value) using AddNewField
      obxSegment.AddNewField(edField, 5);

      // OBX.11 should already be set to 'F' (Final) in the template

      Log.Information("Updated OBX segment ({ObservationIdentifier}) with Base64 {DataType}/{DataSubtype} data",
        observationIdentifier, dataType, dataSubtype);

      return message.SerializeMessage();
    }
    catch (Exception ex)
    {
      Log.Error(ex, "Error adding Base64 data ({ObservationIdentifier}) to OBX segment: {Message}",
        observationIdentifier, ex.Message);
      return messageText; // Return original message if there was an error
    }
  }

  /// <summary>
  ///   Updates the OBX segment specifically for PDF content extracted from DICOM.
  /// </summary>
  /// <param name="messageText">The HL7 message to update</param>
  /// <param name="base64PdfData">Base64 encoded PDF data</param>
  /// <returns>Updated HL7 message string</returns>
  public static string UpdateObxWithPdfFromData(string messageText, string base64PdfData)
  {
    // Use the consistent identifier "47045-0^Study report^LN" for OBX-3
    return UpdateObxWithBase64Data(messageText, base64PdfData, "Application",
      "PDF", "47045-0^Study report^LN");
  }

  /// <summary>
  ///   Adds or updates an OBX segment in the HL7 message to include Base64 encoded DICOM image data.
  /// </summary>
  /// <param name="messageText">The existing HL7 message string</param>
  /// <param name="dataset">The DicomDataset containing image metadata</param>
  /// <param name="base64ImageData">The Base64 encoded string of the DICOM pixel data</param>
  /// <returns>The updated HL7 message string with the embedded image data, or the original string on error.</returns>
  public static string UpdateObxWithImageData(string messageText, DicomDataset dataset, string base64ImageData)
  {
    if (string.IsNullOrEmpty(base64ImageData))
    {
      Log.Warning("Base64 image data is null or empty, cannot add image to OBX segment.");
      return messageText;
    }

    try
    {
      HL7Encoding enc = new();
      Message message = new(messageText);
      message.ParseMessage(); // Ensure message is parsed

      // Find the OBX segment with the Study report observation identifier
      Segment obxSegment = message.Segments("OBX").FirstOrDefault(seg =>
        seg.Fields(3) != null && seg.Fields(3).Value == "47045-0^Study report^LN");

      if (obxSegment == null)
      {
        Log.Warning("Could not find an OBX segment with Study report identifier in the template");
        return messageText;
      }

      // Create the ED field for image content (Base64 encoded)
      Field edField = new(enc);

      // HL7 ED Component structure: Source Application^Type of Data^Data Subtype^Encoding^Data
      edField.AddNewComponent(new Component(enc) { Value = "" }); // Source App (Optional)
      edField.AddNewComponent(new Component(enc) { Value = "AP" }); // Type of Data (Application)
      edField.AddNewComponent(new Component(enc) { Value = "Octet-stream" }); // Data Subtype
      edField.AddNewComponent(new Component(enc) { Value = "Base64" }); // Encoding
      edField.AddNewComponent(new Component(enc) { Value = base64ImageData }); // Data

      // Update OBX.5 (Observation Value) with the encoded image data
      obxSegment.AddNewField(edField, 5);

      // OBX.11 should already be set to 'F' (Final) in the template

      Log.Information("Updated OBX segment with DICOM image data in Base64 format");

      return message.SerializeMessage();
    }
    catch (Exception ex)
    {
      Log.Error(ex, "Error adding DICOM image data to OBX segment: {Message}", ex.Message);
      return messageText; // Return original message if there was an error
    }
  }

  /// <summary>
  ///   Recursively searches for a specific DICOM tag within sequences in a dataset
  /// </summary>
  /// <param name="dataset">The DICOM dataset to search in</param>
  /// <param name="targetTag">The tag to search for</param>
  /// <returns>The value of the first occurrence of the tag found, or empty string if not found</returns>
  private static string FindTagInSequences(DicomDataset dataset, DicomTag targetTag)
  {
    // First check if the tag exists directly in this dataset
    if (dataset.Contains(targetTag) && targetTag.DictionaryEntry.ValueRepresentations[0] != DicomVR.SQ)
      try
      {
        return dataset.GetSingleValueOrDefault(targetTag, "");
      }
      catch
      {
        DicomElement element = dataset.GetDicomItem<DicomElement>(targetTag);
        if (element != null)
          return element.ToString();
      }

    // Then search through all sequences in this dataset
    foreach (DicomItem item in dataset)
      if (item.ValueRepresentation == DicomVR.SQ)
      {
        DicomSequence sequence = dataset.GetSequence(item.Tag);
        foreach (DicomDataset sequenceItem in sequence)
        {
          string result = FindTagInSequences(sequenceItem, targetTag);
          if (!string.IsNullOrEmpty(result))
            return result;
        }
      }

    return "";
  }

  /// <summary>
  ///   Replaces special placeholders that aren't direct DICOM tags
  /// </summary>
  /// <param name="template">The template containing special placeholders</param>
  /// <param name="dataset">The DICOM dataset to extract relevant information from</param>
  /// <returns>Template with special placeholders replaced</returns>
  private static string ReplaceSpecialPlaceholders(string template, DicomDataset dataset)
  {
    // Replace current date/time placeholder with formatted current time
    template = template.Replace("#{CurrentDateTime}", FormatHl7DateTime(DateTime.Now));

    // Replace study date/time placeholder with a combination of study date and time
    // or current time if not available
    template = template.Replace("#{StudyDateTime}", GetStudyDateTime(dataset));

    return template;
  }

  /// <summary>
  ///   Formats a DateTime object according to HL7 standards (YYYYMMDDHHMMSS)
  /// </summary>
  /// <param name="dateTime">The DateTime to format</param>
  /// <returns>HL7 formatted date/time string</returns>
  private static string FormatHl7DateTime(DateTime dateTime) => dateTime.ToString("yyyyMMddHHmmss");

  /// <summary>
  ///   Gets the study date/time from the dataset or returns a default value
  /// </summary>
  /// <param name="dataset">The DICOM dataset to extract study date/time from</param>
  /// <returns>HL7 formatted study date/time</returns>
  private static string GetStudyDateTime(DicomDataset dataset)
  {
    try
    {
      // Try to get study date and time from dataset
      string studyDate = "";
      string studyTime = "";

      DicomTag dateTag = new(0x0008, 0x0020); // Study Date
      DicomTag timeTag = new(0x0008, 0x0030); // Study Time

      if (dataset.Contains(dateTag))
        studyDate = dataset.GetSingleValueOrDefault(dateTag, "");
      if (dataset.Contains(timeTag))
        studyTime = dataset.GetSingleValueOrDefault(timeTag, "");

      // If we have both date and time, combine them
      if (!string.IsNullOrEmpty(studyDate) && !string.IsNullOrEmpty(studyTime))
      {
        // Normalize study time to ensure it has 6 digits (HHMMSS)
        studyTime = NormalizeStudyTime(studyTime);
        return studyDate + studyTime;
      }

      // Default to current time if study time not available
      return FormatHl7DateTime(DateTime.Now);
    }
    catch (Exception ex)
    {
      Log.Error("Error getting study date/time: {Message}", ex.Message);
      return FormatHl7DateTime(DateTime.Now);
    }
  }

  /// <summary>
  ///   Normalizes a study time string to ensure it has 6 digits (HHMMSS)
  /// </summary>
  /// <param name="studyTime">The study time string from DICOM</param>
  /// <returns>Normalized study time string</returns>
  private static string NormalizeStudyTime(string studyTime)
  {
    // Remove any fractional part (after the decimal point)
    string[] parts = studyTime.Split('.');
    studyTime = parts[0];

    // Ensure we have HHMMSS format (six digits)
    switch (studyTime.Length)
    {
      case 2: // HH
        return studyTime + "0000";
      case 4: // HHMM
        return studyTime + "00";
      case 6: // HHMMSS
        return studyTime;
      default:
        // If it's longer than 6, truncate to 6
        if (studyTime.Length > 6)
          return studyTime.Substring(0, 6);

        // If it's odd length or shorter, just pad with zeros
        return studyTime.PadRight(6, '0');
    }
  }
}
