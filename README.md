# OrderORM

A medical order management system that bridges DICOM worklists and HL7 messaging systems. This application queries DICOM worklists for scheduled procedures and generates HL7 ORM messages to forward to compatible receivers.

## Features

- Queries DICOM Modality Worklist (MWL) for scheduled procedures
- Generates HL7 ORM messages from DICOM worklist data
- Implements caching with configurable retention to prevent duplicate submissions
- Supports automatic retry for failed message delivery
- Configurable query parameters (patient name, modality, date ranges)
- Template-based HL7 message generation
- YAML-based configuration

## Requirements

- .NET Framework 4.7.2
- Windows environment (not compatible with macOS/Linux)

## Dependencies

- **fo-dicom**: DICOM implementation for .NET
- **NHapi**: HL7 processing library
- **YamlDotNet**: YAML file parsing
- **Microsoft.Extensions.Logging**: Logging framework

## Project Structure

- **WorklistQuerier.cs**: Queries DICOM worklists using C-FIND requests
- **OrmGenerator.cs**: Generates HL7 ORM messages using configurable templates
- **HL7Sender.cs**: Sends HL7 messages over TCP/IP using MLLP protocol
- **CacheManager.cs**: Handles message caching and prevents duplicates
- **Config.cs**: Configuration management

## Configuration

Configuration is stored in `config.yaml` with the following sections:

```yaml
OrmTemplatePath: Path to HL7 ORM template file
Cache:
  Folder: Cache directory location
  RetentionDays: Number of days to retain cached messages
Dicom:
  ScuAeTitle: DICOM SCU application entity title
  ScpHost: DICOM worklist server host
  ScpPort: DICOM worklist server port
  ScpAeTitle: DICOM worklist server AE title
HL7:
  SenderName: HL7 sender application name
  ReceiverName: HL7 receiver application name
  ReceiverFacility: HL7 receiver facility name
  ReceiverHost: HL7 receiver host
  ReceiverPort: HL7 receiver port
Query:
  ScheduledStationAeTitle: Optional filter for specific station
  ScheduledProcedureStepStartDate: Date configuration
  Modality: Optional filter for specific modality
QueryInterval: Seconds between queries
Retry:
  RetryIntervalMinutes: Minutes between retry attempts
```

## Building and Running

```
dotnet build OrderORM.csproj
dotnet run --project OrderORM.csproj
```

Note: This application is designed to run continuously as a service, querying at configured intervals.

## License

This software is proprietary and confidential. Unauthorized copying, transferring, or reproduction of the contents of this repository, via any medium, is strictly prohibited.

## Support

For issues or questions, please contact the internal development team.
