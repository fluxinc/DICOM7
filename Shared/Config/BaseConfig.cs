using System;

namespace DICOM7.Shared.Config
{
  /// <summary>
  /// Base configuration settings common to all DICOM7 applications
  /// </summary>
  public class BaseConfig : IHasCacheConfig
  {
    /// <summary>
    /// Cache-related configuration settings
    /// </summary>
    public virtual BaseCacheConfig Cache { get; set; }

    /// <summary>
    /// DICOM configuration settings
    /// </summary>
    public virtual BaseDicomConfig Dicom { get; set; }

    /// <summary>
    /// HL7 related configuration settings
    /// </summary>
    public virtual BaseHL7Config HL7 { get; set; }
  }

  /// <summary>
  /// Base configuration settings for the cache folder
  /// </summary>
  public class BaseCacheConfig
  {
    /// <summary>
    /// Custom folder path for cache storage
    /// </summary>
    public string Folder { get; set; }

    /// <summary>
    /// Number of days to retain cached messages
    /// </summary>
    public int RetentionDays { get; set; } = 3;

    /// <summary>
    /// Whether to keep sent/processed items (true) or delete them immediately (false)
    /// </summary>
    public bool KeepSentItems { get; set; } = true;
  }

  /// <summary>
  /// Base configuration settings for DICOM
  /// </summary>
  public class BaseDicomConfig
  {
    /// <summary>
    /// AE Title for the DICOM application
    /// </summary>
    public string AETitle { get; set; } = "DICOM7";
    public int ListenPort { get; set; } = 104;
  }

  /// <summary>
  /// Base configuration settings for HL7 message processing
  /// </summary>
  public class BaseHL7Config
  {
    /// <summary>
    /// Sender name for HL7 messages
    /// </summary>
    public string SenderName { get; set; } = "DICOM7";

    /// <summary>
    /// Receiver name for HL7 messages
    /// </summary>
    public string ReceiverName { get; set; } = "RECEIVER_APPLICATION";

    /// <summary>
    /// Receiver facility for HL7 messages
    /// </summary>
    public string ReceiverFacility { get; set; } = "RECEIVER_FACILITY";
  }
}
