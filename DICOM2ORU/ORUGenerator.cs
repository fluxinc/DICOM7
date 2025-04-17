using System;
using System.IO;
using System.Text.RegularExpressions;
using Efferent.HL7.V2;
using FellowOakDicom;
using Serilog;
using System.Linq;

namespace DICOM2ORU
{
  /// <summary>
  ///     Handles the generation and customization of HL7 ORU messages from DICOM datasets
  /// </summary>
  public class ORUGenerator
  {
    /// <summary>
    ///     Loads an ORU template from the specified file path or returns a default template if not found
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
      Message message = new Message(File.ReadAllText(normalizedPath));
      try
      {
        message.ParseMessage();
      }
      catch (Exception e)
      {
        Log.Fatal(e, "Failed to parse ORU template from '{Path}':", normalizedPath);
        Log.Error("Exiting. Please check the template and restart DICOM2ORU");
        Environment.Exit(1);
      }

      return message.SerializeMessage();
    }

    /// <summary>
    ///     Returns a default HL7 v2.3 ORU message template with DICOM tag placeholders
    /// </summary>
    /// <returns>A string containing the default ORU template</returns>
    private static Message GetDefaultV23OruMessage()
    {
      Message message = new Message();
      message.AddSegmentMSH(
          "DICOM2ORU",
          "Flux Inc",
          "RECEIVER_APPLICATION",
          "RECEIVER_FACILITY",
          "",
          "ORU^R01",
          "#{0020,000D}", // Study Instance UID as message control ID
          "P",
          "2.3");

      HL7Encoding enc = new HL7Encoding();

      Segment pid = new Segment("PID", enc);
      pid.AddNewField("1");                    // PID.1 Set ID
      pid.AddNewField("#{0010,0020}", 2);     // PID.2 Patient ID
      pid.AddNewField("#{0010,0020}", 3);     // PID.3 Patient ID (Alternate)
      pid.AddNewField("#{0010,0010}", 5);     // PID.5 Patient Name
      pid.AddNewField("#{0010,0030}", 7);     // PID.7 Date of Birth
      pid.AddNewField("#{0010,0040}", 8);     // PID.8 Sex
      message.AddNewSegment(pid);

      Segment pv1 = new Segment("PV1", enc);
      pv1.AddNewField("1");                    // PV1.1 Set ID
      pv1.AddNewField("O");                    // PV1.2 Patient Class
      pv1.AddNewField("#{0008,0080}", 3);     // PV1.3 Assigned Location (Institution Name)
      pv1.AddNewField("#{0008,0050}", 19);    // PV1.19 Visit Number (Accession Number)
      message.AddNewSegment(pv1);

      Segment orc = new Segment("ORC", enc);
      orc.AddNewField("RE");                   // ORC.1 Order Control (RE = Observations/Results)
      orc.AddNewField("#{0008,0050}");        // ORC.2 Placer Order Number (Accession Number)
      orc.AddNewField("#{0020,000D}");        // ORC.3 Filler Order Number (Study Instance UID)
      orc.AddNewField("CM");                   // ORC.4 Order Status (CM = Completed)
      message.AddNewSegment(orc);

      Segment obr = new Segment("OBR", enc);
      obr.AddNewField("1");                    // OBR.1 Set ID
      obr.AddNewField("#{0008,0050}", 2);     // OBR.2 Placer Order Number (Accession Number)
      obr.AddNewField("#{0020,000D}", 3);     // OBR.3 Filler Order Number (Study Instance UID)
      obr.AddNewField("#{0008,1030}", 4);     // OBR.4 Universal Service ID (Study Description)
      obr.AddNewField("#{0008,0020}#{0008,0030}", 7); // OBR.7 Observation Date/Time (Study Date + Study Time)
      obr.AddNewField("#{CurrentDateTime}", 8); // OBR.8 Observation End Date/Time
      obr.AddNewField("#{0008,1070}", 16);    // OBR.16 Ordering Provider (Operators' Name)
      message.AddNewSegment(obr);

      // Default OBX for report if available
      Segment obx = new Segment("OBX", enc);
      obx.AddNewField("1");                    // OBX.1 Set ID
      obx.AddNewField("ED");                   // OBX.2 Value Type (ED = Encapsulated Data)
      obx.AddNewField("SR", 3);               // OBX.3 Observation Identifier (SR = Structured Report)
      obx.AddNewField("1", 4);                // OBX.4 Observation Sub-ID
      obx.AddNewField("", 5);                 // OBX.5 Observation Value (will be filled with PDF if available)
      obx.AddNewField("", 11);                // OBX.11 Observation Result Status
      message.AddNewSegment(obx);

      return message;
    }

    /// <summary>
    ///     Replaces DICOM tag placeholders in an ORU template with values from a DICOM dataset
    /// </summary>
    /// <param name="template">The ORU template containing placeholders in the format #{group,element}</param>
    /// <param name="dataset">The DICOM dataset containing values to insert into the template</param>
    /// <returns>A completed HL7 ORU message with placeholders replaced by dataset values</returns>
    public static string ReplacePlaceholders(string template, DicomDataset dataset)
    {
      // First replace special placeholders that aren't direct DICOM tags
      template = ReplaceSpecialPlaceholders(template, dataset);

      // Then replace standard DICOM tag placeholders
      return Regex.Replace(template, @"#\{([0-9a-fA-F]{4}),([0-9a-fA-F]{4})\}", match =>
      {
        try
        {
          string groupStr = match.Groups[1].Value;
          string elementStr = match.Groups[2].Value;
          DicomTag tag = new DicomTag(Convert.ToUInt16(groupStr, 16), Convert.ToUInt16(elementStr, 16));
          // First try to get the value directly from the dataset
          if (dataset.Contains(tag))
          {
            try
            {
              if (tag.DictionaryEntry.ValueRepresentations[0] == DicomVR.SQ)
              {
                // For sequence tags, try to find the first occurrence of this tag in any sequence
                string sequenceValue = FindTagInSequences(dataset, tag);
                if (!string.IsNullOrEmpty(sequenceValue))
                  return sequenceValue.Replace("|", "^");
                return "[Sequence]";
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
              Log.Warning("Unable to get string value for tag ({GroupStr},{ElementStr}): {Message}", groupStr, elementStr, ex.Message);
            }
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
    /// Generic method to updates the OBX segment with Base64 encoded data.
    /// </summary>
    /// <param name="messageText">The HL7 message string</param>
    /// <param name="base64Data">Base64 encoded data string</param>
    /// <param name="dataType">HL7 Data Type component (e.g., "Application")</param>
    /// <param name="dataSubtype">HL7 Data Subtype component (e.g., "PDF")</param>
    /// <param name="observationIdentifier">HL7 Observation Identifier (OBX-3, e.g., "SR" or "PDF_DATA")</param>
    /// <returns>Updated HL7 message string</returns>
    private static string UpdateObxWithBase64Data(string messageText, string base64Data, string dataType, string dataSubtype, string observationIdentifier)
    {
      if (string.IsNullOrEmpty(base64Data))
      {
        Log.Warning("Base64 data is null or empty, cannot add to OBX segment for {ObservationIdentifier}", observationIdentifier);
        return messageText;
      }

      try
      {
        Message message = new Message(messageText);
        message.ParseMessage();

        HL7Encoding enc = new HL7Encoding();
        // Find existing OBX for this identifier or create a new one
        // Use Field(3) to check the Observation Identifier
        Segment obxSegment = message.Segments("OBX").FirstOrDefault(seg =>
            seg.Fields(3) != null && seg.Fields(3).Value == observationIdentifier);

        if (obxSegment == null)
        {
          // Create a new OBX segment if one doesn't exist
          obxSegment = new Segment("OBX", enc);
          obxSegment.AddNewField("1");    // OBX.1 Set ID (Needs logic for multiple OBX)
          obxSegment.AddNewField("ED");   // OBX.2 Value Type
          obxSegment.AddNewField(observationIdentifier, 3); // OBX.3 Observation Identifier
          obxSegment.AddNewField("1", 4);  // OBX.4 Observation Sub-ID
          message.AddNewSegment(obxSegment);
        }

        // Create the ED field for the content
        Field edField = new Field(enc);

        // HL7 ED Component structure: Source Application^Type of Data^Data Subtype^Encoding^Data
        edField.AddNewComponent(new Component(enc) { Value = "" }); // Source App (Optional)
        edField.AddNewComponent(new Component(enc) { Value = dataType }); // Type of Data
        edField.AddNewComponent(new Component(enc) { Value = dataSubtype }); // Data Subtype
        edField.AddNewComponent(new Component(enc) { Value = "Base64" }); // Encoding
        edField.AddNewComponent(new Component(enc) { Value = base64Data }); // Data

        // Set OBX.5 (Observation Value) using AddNewField
        obxSegment.AddNewField(edField, 5);

        // Set OBX.11 (Observation Result Status) to 'F' (Final)
        Field statusField = new Field(enc);
        statusField.Value = "F";
        // Set OBX.11 using AddNewField
        obxSegment.AddNewField(statusField, 11);

        Log.Information("Added Base64 {DataType}/{DataSubtype} attachment to OBX segment ({ObservationIdentifier})", dataType, dataSubtype, observationIdentifier);

        return message.SerializeMessage();
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error adding Base64 data ({ObservationIdentifier}) to OBX segment: {Message}", observationIdentifier, ex.Message);
        return messageText; // Return original message if there was an error
      }
    }

    /// <summary>
    /// Updates the OBX segment specifically for PDF content extracted from DICOM.
    /// </summary>
    /// <param name="messageText">The HL7 message to update</param>
    /// <param name="base64PdfData">Base64 encoded PDF data</param>
    /// <returns>Updated HL7 message string</returns>
    public static string UpdateObxWithPdfFromData(string messageText, string base64PdfData)
    {
      // Use "SR" (Structured Report) or a custom identifier like "PDF_DATA" for OBX-3
      return UpdateObxWithBase64Data(messageText, base64PdfData, "Application", "PDF", "SR");
    }

    /// <summary>
    /// Adds or updates an OBX segment in the HL7 message to include Base64 encoded DICOM image data.
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
        HL7Encoding enc = new HL7Encoding();
        Message message = new Message(messageText);
        message.ParseMessage(); // Ensure message is parsed

        // Find or create an OBX segment for the image data
        // Use Field(3) to check the Observation Identifier
        Segment obxSegment = message.Segments("OBX").FirstOrDefault(seg =>
             seg.Fields(3) != null && seg.Fields(3).Value == "IMG_DATA");

        if (obxSegment == null)
        {
          // Create a new OBX segment if one doesn't exist for image data
          obxSegment = new Segment("OBX", enc);
          obxSegment.AddNewField("1"); // OBX.1 Set ID (increment if multiple OBX exist?)
          obxSegment.AddNewField("ED"); // OBX.2 Value Type
          obxSegment.AddNewField("IMG_DATA", 3); // OBX.3 Observation Identifier
          obxSegment.AddNewField("1", 4); // OBX.4 Observation Sub-ID
          message.AddNewSegment(obxSegment);
        }

        // Create the ED field for image content (Base64 encoded)
        Field edField = new Field(enc);

        // Get relevant metadata for ED components (e.g., image type, encoding)
        string imageType = dataset.GetSingleValueOrDefault(DicomTag.PhotometricInterpretation, "UNK"); // e.g., MONOCHROME2, RGB
        string encoding = "Base64"; // We are encoding as Base64

        // HL7 ED Component structure: Source Application^Type of Data^Data Subtype^Encoding^Data
        // Component 1: Source Application (Optional, can be empty)
        Component component1 = new Component(enc);
        component1.Value = ""; // Or perhaps the AETitle?
        edField.AddNewComponent(component1);

        // Component 2: Type of Data (e.g., Image)
        Component component2 = new Component(enc);
        component2.Value = "Image";
        edField.AddNewComponent(component2);

        // Component 3: Data Subtype (e.g., MONOCHROME2, RGB, JPG - use PhotometricInterpretation)
        Component component3 = new Component(enc);
        component3.Value = imageType;
        edField.AddNewComponent(component3);

        // Component 4: Encoding (Base64)
        Component component4 = new Component(enc);
        component4.Value = encoding;
        edField.AddNewComponent(component4);

        // Component 5: Data (Base64 encoded image data)
        Component component5 = new Component(enc);
        component5.Value = base64ImageData;
        edField.AddNewComponent(component5);

        // Set OBX.5 (Observation Value) using AddNewField
        obxSegment.AddNewField(edField, 5);


        // Set OBX.11 (Observation Result Status) to 'F' (Final)
        Field statusField = new Field(enc);
        statusField.Value = "F";
        // Set OBX.11 using AddNewField
        obxSegment.AddNewField(statusField, 11);

        Log.Information("Added DICOM image data attachment to OBX segment");

        return message.SerializeMessage();
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error adding DICOM image data to OBX segment: {Message}", ex.Message);
        return messageText; // Return original message if there was an error
      }
    }

    /// <summary>
    ///     Recursively searches for a specific DICOM tag within sequences in a dataset
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
    ///     Replaces special placeholders that aren't direct DICOM tags
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
    ///     Formats a DateTime object according to HL7 standards (YYYYMMDDHHMMSS)
    /// </summary>
    /// <param name="dateTime">The DateTime to format</param>
    /// <returns>HL7 formatted date/time string</returns>
    private static string FormatHl7DateTime(DateTime dateTime)
    {
      return dateTime.ToString("yyyyMMddHHmmss");
    }

    /// <summary>
    ///     Gets the study date/time from the dataset or returns a default value
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

        DicomTag dateTag = new DicomTag(0x0008, 0x0020); // Study Date
        DicomTag timeTag = new DicomTag(0x0008, 0x0030); // Study Time

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
    /// Normalizes a study time string to ensure it has 6 digits (HHMMSS)
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
}
