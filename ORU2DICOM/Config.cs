using System;
using DICOM7.Shared.Config;

namespace DICOM7.ORU2DICOM
{
  /// <summary>
  /// Configuration settings for the ORU2DICOM application
  /// </summary>
  public class Config : IHasCacheConfig
  {
    /// <summary>
    /// DICOM worklist SCP configuration
    /// </summary>
    public DicomConfig Dicom { get; set; } = new DicomConfig();

    /// <summary>
    /// HL7 configuration
    /// </summary>
    public HL7Config HL7 { get; set; } = new HL7Config();

    /// <summary>
    /// Cache configuration
    /// </summary>
    public CacheConfig Cache { get; set; } = new CacheConfig();

    /// <summary>
    /// Retry configuration for deferred sends
    /// </summary>
    public RetryConfig Retry { get; set; } = new RetryConfig();

    // Explicit interface implementation to satisfy IHasCacheConfig
    BaseCacheConfig IHasCacheConfig.Cache
    {
      get => Cache;
      set => Cache = value as CacheConfig ?? new CacheConfig
      {
        RetentionDays = value?.RetentionDays ?? 3,
        Folder = value?.Folder,
        KeepSentItems = value?.KeepSentItems ?? true,
        AutoCleanup = true,
        CleanupIntervalMinutes = 60,
        PersistDicomFiles = true
      };
    }
  }

  /// <summary>
  /// DICOM worklist server configuration
  /// </summary>
  public class DicomConfig : BaseDicomConfig
  {
    /// <summary>
    /// Hostname or IP of the target DICOM SCP
    /// </summary>
    public string DestinationHost { get; set; } = "localhost";

    /// <summary>
    /// TCP port for the target DICOM SCP
    /// </summary>
    public int DestinationPort { get; set; } = 104;

    /// <summary>
    /// Called AE Title (remote PACS/service)
    /// </summary>
    public string DestinationAeTitle { get; set; } = "PACS";

    /// <summary>
    /// Calling AE Title (local sender)
    /// </summary>
    public string SourceAeTitle { get; set; } = "ORU2DICOM";

    /// <summary>
    /// Optional Study Description override if not supplied by ORU
    /// </summary>
    public string DefaultStudyDescription { get; set; } = "HL7 ORU Result";

    /// <summary>
    /// Whether to negotiate TLS when sending C-STORE
    /// </summary>
    public bool UseTls { get; set; }
  }

  /// <summary>
  /// HL7 messaging configuration
  /// </summary>
  public class HL7Config : BaseHL7Config
  {
    /// <summary>
    /// TCP port for HL7 listener
    /// </summary>
    public int ListenPort { get; set; } = 7777;

    /// <summary>
    /// IP address to bind HL7 listener
    /// </summary>
    public string ListenIP { get; set; } = "0.0.0.0";

    /// <summary>
    /// Optional acknowledgement code to use when persistence succeeds but send fails (defaults to AA)
    /// </summary>
    public string DeferredAckCode { get; set; } = "AA";
  }

  /// <summary>
  /// Cache and cleanup configuration
  /// </summary>
  public class CacheConfig : BaseCacheConfig
  {
    /// <summary>
    /// Enable automatic cache cleanup
    /// </summary>
    public bool AutoCleanup { get; set; } = true;

    /// <summary>
    /// Cleanup interval in minutes
    /// </summary>
    public int CleanupIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Whether to persist generated DICOM files in the sent folder
    /// </summary>
    public bool PersistDicomFiles { get; set; } = true;
  }

  /// <summary>
  /// Retry configuration for DICOM send attempts
  /// </summary>
  public class RetryConfig
  {
    /// <summary>
    /// Minutes to wait between retry sweeps
    /// </summary>
    public int RetryIntervalMinutes { get; set; } = 2;

    /// <summary>
    /// Maximum number of retry attempts before moving message to error
    /// </summary>
    public int MaxAttempts { get; set; } = 10;
  }
}
