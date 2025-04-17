using System;

namespace DICOM7.ORM2DICOM
{
  /// <summary>
  /// Configuration settings for the ORM2DICOM application
  /// </summary>
  public class Config
  {
    /// <summary>
    /// Cache-related configuration settings
    /// </summary>
    public CacheConfig Cache { get; set; } = new();

    /// <summary>
    /// DICOM worklist SCP configuration settings
    /// </summary>
    public DicomConfig Dicom { get; set; } = new();

    /// <summary>
    /// HL7 related configuration settings
    /// </summary>
    public HL7Config HL7 { get; set; } = new();

    /// <summary>
    /// Time in seconds between processing cycles
    /// </summary>
    public int ProcessInterval { get; set; } = 60;

    /// <summary>
    /// Expiry configuration for cached messages
    /// </summary>
    public ExpiryConfig Expiry { get; set; } = new();
  }

  /// <summary>
  /// Configuration settings for the cache folder
  /// </summary>
  public class CacheConfig
  {
    /// <summary>
    /// Custom folder path for cache storage
    /// </summary>
    public string Folder { get; set; }

    /// <summary>
    /// Number of days to retain cached ORM messages
    /// </summary>
    public int RetentionDays { get; set; } = 3;
  }

  /// <summary>
  /// Configuration settings for DICOM worklist SCP
  /// </summary>
  public class DicomConfig
  {
    /// <summary>
    /// AE Title for the DICOM worklist SCP
    /// </summary>
    public string AETitle { get; set; } = "DICOM7_ORM2DICOM";

    /// <summary>
    /// IP address to bind the DICOM worklist SCP to
    /// </summary>
    public string ListenIP { get; set; } = "0.0.0.0";

    /// <summary>
    /// Port to listen for DICOM worklist requests
    /// </summary>
    public int ListenPort { get; set; } = 11112;

    /// <summary>
    /// Maximum number of concurrent connections
    /// </summary>
    public int MaxConnections { get; set; } = 10;

    /// <summary>
    /// Facility name to include in worklist responses
    /// </summary>
    public string FacilityName { get; set; } = "Flux Inc";
  }

  /// <summary>
  /// Configuration settings for HL7 message processing
  /// </summary>
  public class HL7Config
  {
    /// <summary>
    /// Port to listen for incoming HL7 ORM messages
    /// </summary>
    public int ListenPort { get; set; } = 7777;

    /// <summary>
    /// IP address to bind the HL7 listener to
    /// </summary>
    public string ListenIP { get; set; } = "0.0.0.0";

    /// <summary>
    /// Maximum number of ORMs to store per unique patient ID
    /// </summary>
    public int MaxORMsPerPatient { get; set; } = 5;

    /// <summary>
    /// Sender name for HL7 ACK messages
    /// </summary>
    public string SenderName { get; set; } = "ORM2DICOM";

    /// <summary>
    /// Facility name for HL7 ACK messages
    /// </summary>
    public string FacilityName { get; set; } = "Flux Inc";
  }

  /// <summary>
  /// Configuration settings for message expiry
  /// </summary>
  public class ExpiryConfig
  {
    /// <summary>
    /// Number of hours after which received ORM messages should expire
    /// </summary>
    public int ExpiryHours { get; set; } = 72;

    /// <summary>
    /// Whether to automatically clean up expired messages
    /// </summary>
    public bool AutoCleanup { get; set; } = true;

    /// <summary>
    /// Time in minutes between automatic cleanup runs
    /// </summary>
    public int CleanupIntervalMinutes { get; set; } = 60;
  }
}
