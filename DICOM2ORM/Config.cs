using System;

namespace DICOM7.DICOM2ORM
{
    public class Config
    {
        public CacheConfig Cache { get; set; }
        public DicomConfig Dicom { get; set; }
        public HL7Config HL7 { get; set; }
        public QueryConfig Query { get; set; }
        public int QueryInterval { get; set; } = 60; // In seconds
        public RetryConfig Retry { get; set; } = new RetryConfig(); // Default if not in config
    }

    public class CacheConfig
    {
        public string Folder { get; set; }
        public int RetentionDays { get; set; } = 3;
    }

    public class DicomConfig
    {
        public string ScuAeTitle { get; set; } = "DICOM7";
        public string ScpHost { get; set; } = "worklist.fluxinc.ca";
        public int ScpPort { get; set; } = 1070;
        public string ScpAeTitle { get; set; } = "FLUX_WORKLIST";
    }

    public class HL7Config
    {
    public string SenderName { get; set; } = "DICOM7";
    public string ReceiverName { get; set; } = "RECEIVER_APPLICATION";
    public string ReceiverFacility { get; set; } = "RECEIVER_FACILITY";
    public string ReceiverHost { get; set; } = "localhost";
        public int ReceiverPort { get; set; } = 7777;
    }

    public class QueryConfig
    {
        public string ScheduledStationAeTitle { get; set; }
        public DateConfig ScheduledProcedureStepStartDate { get; set; }
        public string Modality { get; set; }
    }

    public class DateConfig
    {
        public string Mode { get; set; } = "today";
        public int DaysBefore { get; set; } = 1;
        public int DaysAfter { get; set; } = 1;
        public string Date { get; set; } = "";
    }

    public class RetryConfig
    {
        public int RetryIntervalMinutes { get; set; } = 1;
    }
}
