using System;
using System.IO;
using System.Text.RegularExpressions;
using FellowOakDicom;

namespace OrderORM
{
    /// <summary>
    /// Handles the generation and customization of HL7 ORM messages from DICOM datasets
    /// </summary>
    public class OrmGenerator
    {
        /// <summary>
        /// Loads an ORM template from the specified file path or returns a default template if not found
        /// </summary>
        /// <param name="path">The file path where the ORM template is located</param>
        /// <returns>The loaded template string or a default template if file not found</returns>
        public static string LoadTemplate(string path)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"{DateTime.Now} - WARNING: ORM template file not found at '{path}', using default v23 ORM template");
                return GetDefaultV23OrmTemplate();
            }
            Console.WriteLine($"{DateTime.Now} - Reading ORM template from '{path}'");
            return File.ReadAllText(path);
        }

        /// <summary>
        /// Returns a default HL7 v2.3 ORM message template with DICOM tag placeholders
        /// </summary>
        /// <returns>A string containing the default ORM template</returns>
        private static string GetDefaultV23OrmTemplate()
        {
            return @"MSH|^~\&|ORDERORM|#{0008,0080}|RECEIVER|RECEIVER|#{0040,0002}||ORM^O01|#{0020,000D}|P|2.3
PID|1||#{0010,0020}||#{0010,0010}|#{0010,0030}|#{0010,0040}
PV1|1|#{0040,1001}
ORC|NW|#{0008,0050}||#{0020,000D}|SC
OBR|1|#{0008,0050}||#{0008,1030}|#{0040,0100}|#{0040,0002}|#{0040,0003}|||||||#{0040,0100}";
        }

        /// <summary>
        /// Replaces DICOM tag placeholders in an ORM template with values from a DICOM dataset
        /// </summary>
        /// <param name="template">The ORM template containing placeholders in the format #{group,element}</param>
        /// <param name="dataset">The DICOM dataset containing values to insert into the template</param>
        /// <returns>A completed HL7 ORM message with placeholders replaced by dataset values</returns>
        public static string ReplacePlaceholders(string template, DicomDataset dataset)
        {
            return Regex.Replace(template, @"#\{([0-9a-fA-F]{4}),([0-9a-fA-F]{4})\}", match =>
            {
                try
                {
                    string groupStr = match.Groups[1].Value;
                    string elementStr = match.Groups[2].Value;
                    var tag = new DicomTag(Convert.ToUInt16(groupStr, 16), Convert.ToUInt16(elementStr, 16));

                    if (dataset.Contains(tag))
                    {
                        try
                        {
                            if (tag.DictionaryEntry.ValueRepresentations[0] == DicomVR.SQ)
                            {
                                return "[Sequence]";
                            }

                            try
                            {
                                string value = dataset.GetSingleValueOrDefault(tag, "");
                                return value.Replace("|", "^");
                            }
                            catch
                            {
                                var dicomElement = dataset.GetDicomItem<DicomElement>(tag);
                                if (dicomElement != null)
                                {
                                    return dicomElement.ToString().Replace("|", "^");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"{DateTime.Now} - Warning: Unable to get string value for tag ({groupStr},{elementStr}): {ex.Message}");
                        }
                    }
                    return "";
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{DateTime.Now} - Error processing tag in template: {ex.Message}");
                    return "";
                }
            });
        }
    }
}
