using System;
using DICOM7.Shared.Config;

namespace DICOM7.DICOM2ORM
{
    public class Config : IHasCacheConfig
    {
        public BaseCacheConfig Cache { get; set; }
        public DicomConfig Dicom { get; set; } = new DicomConfig(); // Ensure it's initialized with defaults
        public HL7Config HL7 { get; set; } = new HL7Config(); // Also initialize HL7 config
        public QueryConfig Query { get; set; } = new QueryConfig(); // Ensure it's initialized
        public RetryConfig Retry { get; set; } = new RetryConfig(); // Default if not in config
    }


    public class DicomConfig : BaseDicomConfig
    {
        public string ScuAeTitle { get; set; } = "DICOM7";
        public string ScpHost { get; set; } = "worklist.fluxinc.ca";
        public int ScpPort { get; set; } = 1070;
        public string ScpAeTitle { get; set; } = "FLUX_WORKLIST";
    }

    public class HL7Config : BaseHL7Config
    {
        public string ReceiverHost { get; set; } = "localhost";
        public int ReceiverPort { get; set; } = 7777;
    }

    public class QueryConfig
    {
        public string ScheduledStationAeTitle { get; set; }
        public DateConfig ScheduledProcedureStepStartDate { get; set; } = new DateConfig(); // Initialize this too
        public string Modality { get; set; }
        public int IntervalSeconds { get; set; } = 60; // How often to query the worklist
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
