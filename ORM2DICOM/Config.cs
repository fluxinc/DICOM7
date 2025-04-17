using System;
using DICOM7.Shared.Config;

namespace DICOM7.ORM2DICOM
{
  /// <summary>
  /// Configuration settings for the ORM2DICOM application
  /// </summary>
  public class Config : IHasCacheConfig
  {
    /// <summary>
    /// DICOM worklist SCP configuration
    /// </summary>
    public DicomConfig Dicom { get; set; } = new();

    /// <summary>
    /// HL7 configuration
    /// </summary>
    public HL7Config HL7 { get; set; } = new();

    /// <summary>
    /// Order configuration
    /// </summary>
    public OrderConfig Order { get; set; } = new();

    /// <summary>
    /// Cache configuration
    /// </summary>
    public CacheConfig Cache { get; set; } = new();

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
        CleanupIntervalMinutes = 60
      };
    }
  }

  /// <summary>
  /// DICOM worklist server configuration
  /// </summary>
  public class DicomConfig : BaseDicomConfig
  {
    /// <summary>
    /// TCP port for DICOM worklist SCP
    /// </summary>
    public new int ListenPort { get; set; } = 11112;
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
    /// Maximum stored orders per patient
    /// </summary>
    public int MaxORMsPerPatient { get; set; } = 5;
  }

  /// <summary>
  /// Order lifecycle configuration
  /// </summary>
  public class OrderConfig
  {
    /// <summary>
    /// Hours after which an order expires
    /// </summary>
    public int ExpiryHours { get; set; } = 72;
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
  }
}
