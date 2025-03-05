using System;
using System.IO;
using System.Text.RegularExpressions;
using Efferent.HL7.V2;
using FellowOakDicom;
using Serilog;

namespace OrderORM;

/// <summary>
///     Handles the generation and customization of HL7 ORM messages from DICOM datasets
/// </summary>
public class ORMGenerator
{
    /// <summary>
    ///     Loads an ORM template from the specified file path or returns a default template if not found
    /// </summary>
    /// <param name="path">The file path where the ORM template is located</param>
    /// <returns>The loaded template string or a default template if file not found</returns>
    public static string LoadTemplate(string path)
    {
        // Make sure the path is properly normalized for the current OS
        var normalizedPath = Path.GetFullPath(path);
        if (!File.Exists(normalizedPath) )
        {
            Log.Information("Saving default v23 ORM template to '{Path}'", normalizedPath);
            File.WriteAllText(normalizedPath, GetDefaultV23OrmMessage().SerializeMessage());
        }

        Log.Information("Reading ORM template from '{Path}'", normalizedPath);
        var message = new Message(File.ReadAllText(normalizedPath));
        try
        {
            message.ParseMessage();
        } catch (Exception e) {
            Log.Fatal(e, "Failed to parse ORM template from '{Path}':", normalizedPath);
            Log.Error("Exiting.  Please check the template and restart the OrderORM");
            Environment.Exit(1);
        }

        return message.SerializeMessage();
    }

    /// <summary>
    ///     Returns a default HL7 v2.3 ORM message template with DICOM tag placeholders
    /// </summary>
    /// <returns>A string containing the default ORM template</returns>
    private static Message GetDefaultV23OrmMessage()
    {
        var message = new Message();
        message.AddSegmentMSH(
            "ORDERORM",
            "",
            "RECEIVER_APPLICATION",
            "RECEIVER_FACILITY",
            "",
            "ORM^O01",
            "#{0020,000D}",
            "P",
            "2.3");
        var enc = new HL7Encoding();
        var pid = new Segment("PID", enc);
        pid.AddNewField("1");                    // PID.1 Set ID
        pid.AddNewField("#{0010,0020}", 2);     // PID.2 Patient ID
        pid.AddNewField("#{0010,0020}", 3);     // PID.3 Patient ID (Alternate)
        pid.AddNewField("#{0010,0010}", 5);     // PID.5 Patient Name
        pid.AddNewField("#{0010,0030}", 7);     // PID.7 Date of Birth
        pid.AddNewField("#{0010,0040}", 8);     // PID.8 Sex
        message.AddNewSegment(pid);

        var pv1 = new Segment("PV1", enc);
        pv1.AddNewField("1");                    // PV1.1 Set ID
        pv1.AddNewField("O");                    // PV1.2 Patient Class
        pv1.AddNewField("#{0008,0050}", 19);    // PV1.19 Visit Number
        pv1.AddNewField("#{0040,0002}#{0040,0003}", 44); // PV1.44 Admit Date/Time
        message.AddNewSegment(pv1);

        var orc = new Segment("ORC", enc);
        orc.AddNewField("NW");                   // ORC.1 Order Control
        orc.AddNewField("#{0008,0050}");        // ORC.2 Placer Order Number
        orc.AddNewField("#{0020,000D}");        // ORC.3 Filler Order Number
        orc.AddNewField("SC");                   // ORC.4 Order Status
        message.AddNewSegment(orc);

        var obr = new Segment("OBR", enc);
        obr.AddNewField("1");                    // OBR.1 Set ID
        obr.AddNewField("#{0008,0050}", 2);     // OBR.2 Placer Order Number
        obr.AddNewField("#{0008,1030}", 4);     // OBR.4 Universal Service ID
        obr.AddNewField("#{0040,0002}#{0040,0003}", 6); // OBR.6 Requested Date/Time
        message.AddNewSegment(obr);

        return message;
    }

    /// <summary>
    ///     Replaces DICOM tag placeholders in an ORM template with values from a DICOM dataset
    /// </summary>
    /// <param name="template">The ORM template containing placeholders in the format #{group,element}</param>
    /// <param name="dataset">The DICOM dataset containing values to insert into the template</param>
    /// <returns>A completed HL7 ORM message with placeholders replaced by dataset values</returns>
    public static string ReplacePlaceholders(string template, DicomDataset dataset)
    {
        // First replace special placeholders that aren't direct DICOM tags
        template = ReplaceSpecialPlaceholders(template, dataset);

        // Then replace standard DICOM tag placeholders
        return Regex.Replace(template, @"#\{([0-9a-fA-F]{4}),([0-9a-fA-F]{4})\}", match =>
        {
            try
            {
                var groupStr = match.Groups[1].Value;
                var elementStr = match.Groups[2].Value;
                var tag = new DicomTag(Convert.ToUInt16(groupStr, 16), Convert.ToUInt16(elementStr, 16));
                // First try to get the value directly from the dataset
                if (dataset.Contains(tag))
                {
                    try
                    {
                        if (tag.DictionaryEntry.ValueRepresentations[0] == DicomVR.SQ)
                        {
                            // For sequence tags, try to find the first occurrence of this tag in any sequence
                            var sequenceValue = FindTagInSequences(dataset, tag);
                            if (!string.IsNullOrEmpty(sequenceValue)) return sequenceValue.Replace("|", "^");
                            return "[Sequence]";
                        }

                        try
                        {
                            var value = dataset.GetSingleValueOrDefault(tag, "");
                            return value.Replace("|", "^");
                        }
                        catch
                        {
                            var dicomElement = dataset.GetDicomItem<DicomElement>(tag);
                            if (dicomElement != null) return dicomElement.ToString().Replace("|", "^");
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
                    var sequenceValue = FindTagInSequences(dataset, tag);
                    if (!string.IsNullOrEmpty(sequenceValue)) return sequenceValue.Replace("|", "^");
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
                var element = dataset.GetDicomItem<DicomElement>(targetTag);
                if (element != null) return element.ToString();
            }

        // Then search through all sequences in this dataset
        foreach (var item in dataset)
            if (item.ValueRepresentation == DicomVR.SQ)
            {
                var sequence = dataset.GetSequence(item.Tag);
                foreach (var sequenceItem in sequence)
                {
                    var result = FindTagInSequences(sequenceItem, targetTag);
                    if (!string.IsNullOrEmpty(result)) return result;
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

        // Replace scheduled date/time placeholder with a combination of scheduled date and time
        // or current time + 24 hours if not available
        template = template.Replace("#{ScheduledDateTime}", GetScheduledDateTime(dataset));

        // Replace Scheduled Procedure Step ID placeholder
        template = template.Replace("#{ScheduledProcedureStepID}", GetScheduledProcedureStepID(dataset));

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
    ///     Gets the scheduled date/time from the dataset or returns a default value
    /// </summary>
    /// <param name="dataset">The DICOM dataset to extract scheduled date/time from</param>
    /// <returns>HL7 formatted scheduled date/time</returns>
    private static string GetScheduledDateTime(DicomDataset dataset)
    {
        try
        {
            // Try to get scheduled date and time from dataset
            var scheduledDate = "";
            var scheduledTime = "";

            var dateTag = new DicomTag(0x0040, 0x0002); // Scheduled Procedure Step Start Date
            var timeTag = new DicomTag(0x0040, 0x0003); // Scheduled Procedure Step Start Time

            if (dataset.Contains(dateTag)) scheduledDate = dataset.GetSingleValueOrDefault(dateTag, "");

            if (dataset.Contains(timeTag)) scheduledTime = dataset.GetSingleValueOrDefault(timeTag, "");

            // If we have both date and time, combine them
            if (!string.IsNullOrEmpty(scheduledDate) && !string.IsNullOrEmpty(scheduledTime))
                // Format may need adjustment based on the exact format in the DICOM dataset
                return scheduledDate + scheduledTime;

            // Default to current time + 24 hours if scheduled time not available
            return FormatHl7DateTime(DateTime.Now.AddHours(24));
        }
        catch (Exception ex)
        {
            Log.Error("Error getting scheduled date/time: {Message}", ex.Message);
            return FormatHl7DateTime(DateTime.Now.AddHours(24));
        }
    }

    /// <summary>
    /// Extracts the Scheduled Procedure Step ID from the dataset
    /// </summary>
    /// <param name="dataset">The DICOM dataset to extract the ID from</param>
    /// <returns>The Scheduled Procedure Step ID or empty string if not found</returns>
    private static string GetScheduledProcedureStepID(DicomDataset dataset)
    {
        try
        {
            // Tag for Scheduled Procedure Step Sequence
            var spsSequenceTag = new DicomTag(0x0040, 0x0100);

            if (dataset.Contains(spsSequenceTag))
            {
                try
                {
                    // Try to get the sequence
                    var sequence = dataset.GetSequence(spsSequenceTag);

                    // If sequence has items, try to get the ID from the first item
                    if (sequence != null && sequence.Items.Count > 0)
                    {
                        var firstItem = sequence.Items[0];

                        // Tag for Scheduled Procedure Step ID within the sequence
                        var stepIDTag = new DicomTag(0x0040, 0x0009);

                        if (firstItem.Contains(stepIDTag))
                        {
                            return firstItem.GetSingleValueOrDefault(stepIDTag, "");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // If we get an exception, the tag might not be a sequence
                    Log.Warning("Tag (0040,0100) is not a sequence or could not be processed: {Message}", ex.Message);
                }
            }

            // If we can't find it in the sequence, try to find it directly (though this is less likely)
            var directStepIDTag = new DicomTag(0x0040, 0x0009);

            return dataset.Contains(directStepIDTag) ? dataset.GetSingleValueOrDefault(directStepIDTag, "") : "";
        }
        catch (Exception ex)
        {
            Log.Error("Error getting Scheduled Procedure Step ID: {Message}", ex.Message);
            return "";
        }
    }
}
