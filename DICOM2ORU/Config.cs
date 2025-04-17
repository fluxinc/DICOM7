using System;

namespace DICOM2ORU
{
  public class Config
  {
    public CacheConfig Cache { get; set; }
    public DicomConfig Dicom { get; set; }
    public HL7Config HL7 { get; set; }
    public InputConfig Input { get; set; }
    public int ProcessInterval { get; set; } = 60; // In seconds
    public RetryConfig Retry { get; set; } = new RetryConfig(); // Default if not in config
  }

  public class CacheConfig
  {
    public string Folder { get; set; }
    public int RetentionDays { get; set; } = 3;
  }

  public class DicomConfig
  {
    public string ApplicationName { get; set; } = "DICOM2ORU";
    public string FacilityName { get; set; } = "Flux Inc";
  }

  public class HL7Config
  {
    public string SenderName { get; set; } = "DICOM2ORU";
    public string ReceiverName { get; set; } = "RECEIVER_APPLICATION";
    public string ReceiverFacility { get; set; } = "RECEIVER_FACILITY";
    public string ReceiverHost { get; set; } = "localhost";
    public int ReceiverPort { get; set; } = 7777;
    public bool WaitForAck { get; set; } = true;
  }

  public class InputConfig
  {
    public string InputFolder { get; set; } = "input";
    public string ArchiveFolder { get; set; } = "archive";
    public string ErrorFolder { get; set; } = "error";
  }

  public class RetryConfig
  {
    public int RetryIntervalMinutes { get; set; } = 1;
  }
}
