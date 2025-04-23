using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Efferent.HL7.V2;
using FellowOakDicom;
using Serilog;

namespace DICOM7.ORM2DICOM
{
  /// <summary>
  /// Represents the state of a cached ORM message
  /// </summary>
  public enum CachedORMState
  {
    /// <summary>
    /// Active ORM message that can be used for worklist responses
    /// </summary>
    Active
  }

  /// <summary>
  /// Manages the caching and processing of HL7 ORM messages
  /// </summary>
  public class CachedORM
  {
    private static readonly ILogger Logger = Log.ForContext<CachedORM>();

    /// <summary>
    /// File information for the cached ORM message
    /// </summary>
    public FileInfo FileInfo { get; private set; }

    /// <summary>
    /// Raw text content of the ORM message
    /// </summary>
    public string Text { get; private set; }

    /// <summary>
    /// Unique identifier for the message, derived from its content
    /// </summary>
    public string UUID { get; set; }

    /// <summary>
    /// Creates a CachedORM from an existing file
    /// </summary>
    /// <param name="fileInfo">File information for the ORM message</param>
    public CachedORM(FileInfo fileInfo)
    {
      if (fileInfo == null)
        throw new ArgumentNullException(nameof(fileInfo));

      FileInfo = fileInfo;
      Load();

      // Verify the file is in the active directory
      string activePath = GetStatePath(CachedORMState.Active);
      if (FileInfo.Directory.FullName != activePath)
        throw new ArgumentException($"File is not in the active ORM directory: {FileInfo.Directory.FullName}");
    }

    /// <summary>
    /// Creates a new CachedORM from HL7 message text
    /// </summary>
    /// <param name="hl7Data">The raw HL7 message text</param>
    public CachedORM(string hl7Data)
    {
      if (string.IsNullOrEmpty(hl7Data))
        throw new ArgumentNullException(nameof(hl7Data));

      Text = hl7Data;
      UUID = GetHashString(Text);

      string filePath = Path.Combine(GetStatePath(CachedORMState.Active), $"{UUID}.hl7");
      FileInfo = new FileInfo(filePath);
    }

    /// <summary>
    /// Loads the ORM message text from the file
    /// </summary>
    /// <returns>True if load was successful, false otherwise</returns>
    public bool Load()
    {
      try
      {
        if (!FileInfo.Exists)
          throw new FileNotFoundException($"ORM file not found: {FileInfo.FullName}");

        Text = File.ReadAllText(FileInfo.FullName);
        UUID = GetHashString(Text);

        return true;
      }
      catch (Exception e)
      {
        Logger.Error(e, "Failed to load ORM file: {FileName}", FileInfo.Name);
        return false;
      }
    }

    /// <summary>
    /// Updates the last modified time of the file
    /// </summary>
    /// <param name="includeCreationTime">Whether to update creation time as well</param>
    public void Touch(bool includeCreationTime = false)
    {
      try
      {
        if (!FileInfo.Exists)
          return;
        File.SetLastWriteTimeUtc(FileInfo.FullName, DateTime.UtcNow);
        if (includeCreationTime) File.SetCreationTimeUtc(FileInfo.FullName, DateTime.UtcNow);
      }
      catch (Exception e)
      {
        Logger.Error(e, "Error updating timestamp for ORM {UUID}", UUID);
      }
    }

    /// <summary>
    /// Saves the ORM message to its file
    /// </summary>
    /// <returns>True if save was successful, false otherwise</returns>
    public bool Save()
    {
      try
      {
        string statePath = GetStatePath(CachedORMState.Active);

        // Ensure the directory exists
        if (!Directory.Exists(statePath))
        {
          Directory.CreateDirectory(statePath);
        }

        // If file already exists, just touch it
        if (File.Exists(FileInfo.FullName))
        {
          Logger.Information("ORM file for hash '{UUID}' already exists, touching instead of re-writing", UUID);
          Touch();
          return true;
        }

        // Write to temporary file first, then move
        string tmpPath = $"{FileInfo.FullName}.tmp";
        File.WriteAllText(tmpPath, Text);

        if (File.Exists(FileInfo.FullName))
          File.Delete(FileInfo.FullName);

        File.Move(tmpPath, FileInfo.FullName);

        Logger.Information("Saved ORM message {UUID} to {FilePath}", UUID, FileInfo.FullName);
        return true;
      }
      catch (Exception e)
      {
        Logger.Error(e, "Failed to save ORM message {UUID}", UUID);
        return false;
      }
    }

    /// <summary>
    /// Converts the ORM message to a DicomDataset
    /// </summary>
    /// <returns>A DicomDataset with fields mapped from the ORM message</returns>
    public DicomDataset AsDicomDataset()
    {
      try
      {
        // Parse the HL7 message using Efferent.HL7.V2
        Message message = new Message(Text);

        try
        {
          message.ParseMessage();
        }
        catch (Exception e)
        {
          Logger.Error(e, "Failed to parse ORM message {UUID}", UUID);
          return null;
        }

        DicomDataset dataset = new DicomDataset();
        DicomDataset spsDataset = new DicomDataset(); // Scheduled Procedure Step Sequence

        // Get message segments - using message.Segments() method in Efferent.HL7.V2
        Segment msh = message.Segments("MSH").FirstOrDefault();
        Segment pid = message.Segments("PID").FirstOrDefault();
        Segment pv1 = message.Segments("PV1").FirstOrDefault();
        Segment orc = message.Segments("ORC").FirstOrDefault();
        Segment obr = message.Segments("OBR").FirstOrDefault();

        if (msh == null)
        {
          Logger.Warning("MSH segment not found in ORM message {UUID}", UUID);
          return null;
        }

        if (pid == null)
        {
          Logger.Warning("PID segment not found in ORM message {UUID}", UUID);
          return null;
        }

        // Extract patient information
        string patientId = GetFieldValue(pid, 3); // Patient ID
        string patientName = GetFieldComponentsAsPN(pid, 5); // Patient Name
        string birthDate = GetFieldValue(pid, 7); // Date of Birth
        string patientSex = GetFieldValue(pid, 8); // Sex

        // Add patient information to dataset
        dataset.AddOrUpdate(DicomTag.PatientID, TruncateForVR(patientId, DicomTag.PatientID));
        dataset.AddOrUpdate(DicomTag.PatientName, TruncateForVR(patientName, DicomTag.PatientName));
        dataset.AddOrUpdate(DicomTag.PatientBirthDate, birthDate);
        dataset.AddOrUpdate(DicomTag.PatientSex, TruncateForVR(patientSex, DicomTag.PatientSex));

        // Add patient address if available
        string patientAddress = GetFieldValue(pid, 11);
        if (!string.IsNullOrEmpty(patientAddress))
        {
          dataset.AddOrUpdate(DicomTag.PatientAddress, TruncateForVR(patientAddress, DicomTag.PatientAddress));
        }

        // Add visit information if PV1 segment exists
        if (pv1 != null)
        {
          string referringPhysician = GetFieldComponentsAsXCN(pv1, 8);
          if (!string.IsNullOrEmpty(referringPhysician))
          {
            dataset.AddOrUpdate(DicomTag.ReferringPhysicianName, TruncateForVR(referringPhysician, DicomTag.ReferringPhysicianName));
          }

          string institutionName = GetFieldComponentValue(pv1, 3, 4);
          if (!string.IsNullOrEmpty(institutionName))
          {
            dataset.AddOrUpdate(DicomTag.InstitutionName, TruncateForVR(institutionName, DicomTag.InstitutionName));
          }

          // Scheduled Performing Physician
          string scheduledPhysician = GetFieldComponentsAsXCN(pv1, 7);
          if (!string.IsNullOrEmpty(scheduledPhysician))
          {
            spsDataset.AddOrUpdate(DicomTag.ScheduledPerformingPhysicianName, TruncateForVR(scheduledPhysician, DicomTag.ScheduledPerformingPhysicianName));
          }
        }

        // Add order information if ORC segment exists
        if (orc != null)
        {
          string accessionNumber = GetFieldComponentValue(orc, 2, 1);
          if (!string.IsNullOrEmpty(accessionNumber))
          {
            dataset.AddOrUpdate(DicomTag.AccessionNumber, TruncateForVR(accessionNumber, DicomTag.AccessionNumber));
          }

          string requestingPhysician = GetFieldComponentsAsXCN(orc, 12);
          if (!string.IsNullOrEmpty(requestingPhysician))
          {
            dataset.AddOrUpdate(DicomTag.RequestingPhysician, TruncateForVR(requestingPhysician, DicomTag.RequestingPhysician));
          }
        }

        // Add procedure information if OBR segment exists
        if (obr != null)
        {
          // Use OBR-7 for scheduled procedure start date
          string observationDateTime = GetFieldValue(obr, 7);
          if (!string.IsNullOrEmpty(observationDateTime) && observationDateTime.Length >= 8)
          {
            spsDataset.AddOrUpdate(DicomTag.ScheduledProcedureStepStartDate, observationDateTime.Substring(0, 8));
          }

          // Add procedure description from OBR-4
          string procedureDesc = GetFieldValue(obr, 4);
          if (!string.IsNullOrEmpty(procedureDesc))
          {
            spsDataset.AddOrUpdate(DicomTag.ScheduledProcedureStepDescription, TruncateForVR(procedureDesc, DicomTag.ScheduledProcedureStepDescription));
          }
        }

        // Add the Scheduled Procedure Step Sequence
        dataset.AddOrUpdate(new DicomSequence(DicomTag.ScheduledProcedureStepSequence, spsDataset));

        return dataset;
      }
      catch (Exception e)
      {
        Logger.Error(e, "Error converting ORM to DicomDataset for {UUID}", UUID);
        return null;
      }
    }

    /// <summary>
    /// Gets all active CachedORM instances
    /// </summary>
    /// <returns>A collection of active CachedORM instances</returns>
    public static IEnumerable<CachedORM> GetActiveORMs()
    {
      List<CachedORM> result = new List<CachedORM>();
      string statePath = GetStatePath(CachedORMState.Active);

      if (!Directory.Exists(statePath))
      {
        return result;
      }

      // Get all .hl7 files in the directory (excluding .tmp files)
      foreach (FileInfo fileInfo in new DirectoryInfo(statePath)
        .GetFiles("*.hl7")
        .Where(f => !f.Name.EndsWith(".tmp"))
        .OrderBy(f => f.LastWriteTime))
      {
        try
        {
          result.Add(new CachedORM(fileInfo));
        }
        catch (Exception e)
        {
          Logger.Error(e, "Error loading ORM file: {FileName}", fileInfo.Name);
        }
      }

      return result;
    }

    /// <summary>
    /// Removes expired ORM messages
    /// </summary>
    /// <param name="expiryHours">Number of hours after which messages should be considered expired</param>
    /// <returns>The number of expired messages removed</returns>
    public static int RemoveExpired(int expiryHours = 72)
    {
      if (expiryHours < 1)
      {
        return 0;
      }

      int removed = 0;
      DateTime now = DateTime.UtcNow;
      double expirySeconds = expiryHours * 60 * 60;

      // Check active messages
      foreach (CachedORM orm in GetActiveORMs())
      {
        FileInfo file = orm.FileInfo;
        double age = (now - file.LastWriteTimeUtc).TotalSeconds;

        if (age > expirySeconds)
        {
          Logger.Information("ORM message '{FileName}' is {ExpiryHours} hours old, deleting it",
            file.Name, expiryHours);

          try
          {
            file.Delete();
            removed++;
          }
          catch (Exception e)
          {
            Logger.Error(e, "Could not delete expired ORM message: {FileName}", file.Name);
          }
        }
      }

      return removed;
    }

    #region Utility Methods

    /// <summary>
    /// Gets the path for the specified ORM state
    /// </summary>
    /// <param name="state">The state to get the path for</param>
    /// <returns>The full path for the state</returns>
    private static string GetStatePath(CachedORMState state) => Path.Combine(CacheManager.CacheFolder, "active");

    /// <summary>
    /// Gets a hash string for a given input string
    /// </summary>
    /// <param name="input">The input string to hash</param>
    /// <returns>A string representation of the hash</returns>
    private static string GetHashString(string input)
    {
      using (SHA256 sha = System.Security.Cryptography.SHA256.Create())
      {
        byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return BitConverter.ToString(hash).Replace("-", "").Substring(0, 32);
      }

    }

    /// <summary>
    /// Gets the value of a field from a segment
    /// </summary>
    /// <param name="segment">The segment to get the field from</param>
    /// <param name="fieldIndex">The 1-based index of the field</param>
    /// <returns>The field value or empty string if field doesn't exist</returns>
    private string GetFieldValue(Segment segment, int fieldIndex)
    {
      if (segment == null || fieldIndex < 1)
      {
        return string.Empty;
      }

      try
      {
        return segment.Fields(fieldIndex)?.Value ?? string.Empty;
      }
      catch
      {
        return string.Empty;
      }
    }

    /// <summary>
    /// Gets a component value from a field
    /// </summary>
    /// <param name="segment">The segment containing the field</param>
    /// <param name="fieldIndex">The 1-based index of the field</param>
    /// <param name="componentIndex">The 1-based index of the component</param>
    /// <returns>The component value or empty string if not found</returns>
    private static string GetFieldComponentValue(Segment segment, int fieldIndex, int componentIndex)
    {
      if (segment == null || fieldIndex < 1 || componentIndex < 1)
      {
        return string.Empty;
      }

      try
      {
        Field field = segment.Fields(fieldIndex);
        if (field != null && componentIndex <= field.Components().Count)
        {
          return field.Components()[componentIndex - 1].Value ?? string.Empty;
        }
      }
      catch
      {
        // Ignore exceptions and return empty string
      }

      return string.Empty;
    }

    /// <summary>
    /// Formats XPN (Personal Name) field components for DICOM use
    /// </summary>
    /// <param name="segment">The segment containing the field</param>
    /// <param name="fieldIndex">The 1-based index of the field</param>
    /// <returns>Formatted person name for DICOM</returns>
    private static string GetFieldComponentsAsPN(Segment segment, int fieldIndex)
    {
      if (segment == null)
      {
        return string.Empty;
      }

      string familyName = GetFieldComponentValue(segment, fieldIndex, 1); // Family name
      string givenName = GetFieldComponentValue(segment, fieldIndex, 2);  // Given name
      string middleName = GetFieldComponentValue(segment, fieldIndex, 3); // Middle name
      string prefix = GetFieldComponentValue(segment, fieldIndex, 5);     // Prefix
      string suffix = GetFieldComponentValue(segment, fieldIndex, 4);     // Suffix (note order swapped for DICOM)

      // Combine components with ^ separator for DICOM
      List<string> components = new List<string>();

      if (!string.IsNullOrEmpty(familyName)) components.Add(familyName);
      if (!string.IsNullOrEmpty(givenName)) components.Add(givenName);
      if (!string.IsNullOrEmpty(middleName)) components.Add(middleName);
      if (!string.IsNullOrEmpty(prefix)) components.Add(prefix);
      if (!string.IsNullOrEmpty(suffix)) components.Add(suffix);

      return string.Join("^", components);
    }

    /// <summary>
    /// Formats XCN (Extended Composite ID Number and Name) field components for DICOM use
    /// </summary>
    /// <param name="segment">The segment containing the field</param>
    /// <param name="fieldIndex">The 1-based index of the field</param>
    /// <returns>Formatted name for DICOM</returns>
    private string GetFieldComponentsAsXCN(Segment segment, int fieldIndex)
    {
      if (segment == null)
      {
        return string.Empty;
      }

      string idNumber = GetFieldComponentValue(segment, fieldIndex, 1); // ID number (not used in person name)
      string familyName = GetFieldComponentValue(segment, fieldIndex, 2); // Family name
      string givenName = GetFieldComponentValue(segment, fieldIndex, 3);  // Given name
      string middleName = GetFieldComponentValue(segment, fieldIndex, 4); // Middle name
      string suffix = GetFieldComponentValue(segment, fieldIndex, 5);     // Suffix
      string prefix = GetFieldComponentValue(segment, fieldIndex, 6);     // Prefix (note order swapped for DICOM)

      // Combine components with ^ separator for DICOM
      List<string> components = new List<string>();

      if (!string.IsNullOrEmpty(familyName)) components.Add(familyName);
      if (!string.IsNullOrEmpty(givenName)) components.Add(givenName);
      if (!string.IsNullOrEmpty(middleName)) components.Add(middleName);
      if (!string.IsNullOrEmpty(prefix)) components.Add(prefix);
      if (!string.IsNullOrEmpty(suffix)) components.Add(suffix);

      return string.Join("^", components);
    }

    /// <summary>
    /// Truncates a string to fit within the character limits of the specified DICOM Value Representation
    /// </summary>
    /// <param name="value">The string value to truncate if needed</param>
    /// <param name="tag">The DICOM tag, used to determine the VR</param>
    /// <returns>The truncated string that fits within VR limits</returns>
    private string TruncateForVR(string value, DicomTag tag)
    {
      if (string.IsNullOrEmpty(value))
        return value;

      DicomVR vr = tag.DictionaryEntry.ValueRepresentations.First();
      int maxLength;

      switch (vr.Code)
      {
        case "AE": maxLength = 16; break;  // Application Entity
        case "LO": maxLength = 64; break;  // Long String
        case "LT": maxLength = 10240; break; // Long Text
        case "PN": maxLength = 64; break;  // Person Name (per component)
        case "SH": maxLength = 16; break;  // Short String
        case "ST": maxLength = 1024; break; // Short Text
        case "UT": maxLength = int.MaxValue; break; // Unlimited Text
        default: return value; // No truncation for other VRs
      }

      if (value.Length > maxLength)
      {
        Logger.Warning("Truncating value for {Tag} ({VR}) from {OriginalLength} to {MaxLength} characters",
            tag, vr.Code, value.Length, maxLength);
        return value.Substring(0, maxLength);
      }

      return value;
    }

    #endregion
  }
}
