# OrderORM

Order ORM queries DICOM Modality Worklist at a set interval, generates HL7 ORM orders, and sends them to a receiver.

## License

This software is released under the **Medical Software Academic and Restricted Use License**:

- **Academic Users**: Free to download, use, and modify for non-commercial purposes with attribution.
- **Commercial Use**: Prohibited without written permission from Flux Inc.
- **Competitors**: Prohibited from use.
- **Clients**: Prohibited from use by clients of licensees.

See the [LICENSE](LICENSE.md) file for full terms.


## Features

- Queries DICOM Modality Worklist (MWL) for scheduled procedures
- Generates HL7 ORM messages from DICOM worklist data using customizable templates
- Implements caching with configurable retention to prevent duplicate submissions and manage disk space
- Supports automatic retry for failed message deliveries with indefinite retries
- Configurable query parameters (modality, date ranges, station AE title)
- Template-based HL7 message generation with support for a default template
- YAML-based configuration
- Logging to console and file for easier troubleshooting

## Requirements

- .NET Framework 4.7.2
- Windows environment (not compatible with macOS/Linux)

## Why .NET Framework 4.7.2?

- **Windows 10 Compatibility**: Most use cases are for legacy medical software frequently installed on Windows 10, and this only supports .NET <= 4.7.2.

## Dependencies

- **fo-dicom**: DICOM implementation for .NET
- **Serilog**: Logging framework
- **YamlDotNet**: YAML file parsing

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

## HL7 Template

The application uses an HL7 ORM template file (`ormTemplate.hl7`) located in the same directory as `config.yaml`. If not found, a default HL7 v2.3 ORM template is used with placeholders for DICOM tags and special values (e.g., #{CurrentDateTime}, #{ScheduledDateTime}).


## Building and Running

```
msbuild
```

Note: This application is designed to run continuously as a service, querying at configured intervals.

## Command Line Arguments

The application supports the following command line arguments:

- `--path <directory_path>`: Sets a custom base path for the application. All other paths (configuration, cache, logs) will be relative to this path unless explicitly configured otherwise in the config.yaml file.

Example:
```
OrderORM.exe --path C:\CustomPath\OrderORM
```

## License

This software is proprietary and confidential. Unauthorized copying, transferring, or reproduction of the contents of this repository, via any medium, is strictly prohibited.

## Support

For issues or questions, please contact the internal development team.
