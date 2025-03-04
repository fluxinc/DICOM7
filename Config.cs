using System;

namespace OrderORM
{
    public class Config
    {
        public string OrmTemplatePath { get; set; }
        public CacheConfig Cache { get; set; }
        public DicomConfig Dicom { get; set; }
        public HL7Config HL7 { get; set; }
        public QueryConfig Query { get; set; }
        public int QueryInterval { get; set; } // In seconds
        public RetryConfig Retry { get; set; } = new RetryConfig(); // Default if not in config
    }

    public class CacheConfig
    {
        public string Folder { get; set; }
        public int RetentionDays { get; set; }
    }

    public class DicomConfig
    {
        public string ScuAeTitle { get; set; }
        public string ScpHost { get; set; }
        public int ScpPort { get; set; }
        public string ScpAeTitle { get; set; }
    }

    public class HL7Config
    {
        public string ReceiverHost { get; set; }
        public int ReceiverPort { get; set; }
    }

    public class QueryConfig
    {
        public string PatientName { get; set; }
        public string ScheduledStationAeTitle { get; set; }
        public DateConfig ScheduledProcedureStepStartDate { get; set; }
        public string Modality { get; set; }
    }

    public class DateConfig
    {
        public string Mode { get; set; }
        public int DaysBefore { get; set; }
        public int DaysAfter { get; set; }
        public string Date { get; set; }
    }
    
    public class RetryConfig
    {
        public int RetryIntervalMinutes { get; set; } = 5;
    }
}