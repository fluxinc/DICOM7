using System;
using DICOM7.Shared.Config;

namespace DICOM7.DICOM2ORU
{
  public class Config : IHasCacheConfig
  {
    public CacheConfig Cache { get; set; } = new CacheConfig();
    public DicomConfig Dicom { get; set; } = new DicomConfig();
    public HL7Config HL7 { get; set; } = new HL7Config();

    public RetryConfig Retry { get; set; } = new RetryConfig();

    // Type conversion to satisfy IHasCacheConfig interface
    BaseCacheConfig IHasCacheConfig.Cache
    {
      get => Cache;
      set => Cache = value as CacheConfig ?? new CacheConfig
      {
        RetentionDays = value?.RetentionDays ?? 7,
        Folder = value?.Folder,
        KeepSentItems = value?.KeepSentItems ?? true
      };
    }
  }

  public class CacheConfig : BaseCacheConfig
  {
    // Additional cache config properties specific to DICOM2ORU
  }

  public class DicomConfig : BaseDicomConfig
  {
    public new int ListenPort { get; set; } = 104;
  }

  public class HL7Config : BaseHL7Config
  {
    // SenderName, ReceiverName, and ReceiverFacility are inherited from BaseHL7Config
    public string ReceiverHost { get; set; } = "localhost";
    public int ReceiverPort { get; set; } = 7777;
    public bool WaitForAck { get; set; } = true;
  }

  public class RetryConfig
  {
    public int RetryIntervalMinutes { get; set; } = 1;
  }
}
