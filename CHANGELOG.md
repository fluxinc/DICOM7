# Changelog

All notable changes to the DICOM7 project will be documented in this file.

## [Unreleased]

## [2.1.0] - 2025-10-14

### Added

- Included ORU2DICOM binaries, config, and WinSW wrapper in the Windows installer package

### Changed

- Documented ORU2DICOM deployment details in both `README.md` and `installer/Input/readme.txt`

## [2.0.2] - 2025-10-14

### Added

- Added ORU2DICOM component for transforming inbound ORU results into DICOM artefacts
- Added ORM2DICOM.Tests project with initial fixtures to exercise the worklist pipeline
- Added DICOM2ORU component
- Added ORM2DICOM component

### Changed

- Documented each installer component, configuration surface, and deployment task list in `installer/Input/readme.txt`
- Updated sample configuration defaults so DICOM2ORM and DICOM2ORU explicitly surface `Cache.KeepSentItems`, and ORM2DICOM binds HL7 on the configured `ListenIP`
- Renamed to DICOM7
- Downgraded to .NET 4.6.2 for compatibility with older Windows versions
- Upgraded NuGet packages to latest versions

## [1.0.4] - 2025-03-10

### Added

- Added support for custom base path via `--path`command line argument
- HL7-V2 message generation and validation

### Changed

- Writes default ORM template to disk if it doesn't exist

### Fixed

- Some attributes in the default template

## [1.0.3] - 2025-03-04

### Added

- Integrated Serilog for structured logging with console and file outputs
- Added new HL7 sender and receiver configuration options
- Added Serilog and Newtonsoft.Json package dependencies

### Changed

- Enhanced error handling and logging across multiple components
- Updated configuration structure with new HL7 sender and receiver details
- Removed deprecated configuration options

## [1.0.2] - 2025-03-04

### Added

- Example HL7 template with placeholder values

### Changed

- Modified installer configuration to prevent overwriting existing config.yaml
- Renamed ormTemplate.hl7 to ormTemplate.example.hl7 to clarify its purpose

## [1.0.1] - 2025-03-04

### Changed

- Update .gitignore to exclude installer output directory

## [1.0.0] - 2025-03-04

### Added

- Initial release of DICOM2ORM application
- DICOM worklist query functionality
- HL7 ORM message generation from DICOM worklist data
- Caching mechanism with configurable retention period
- Windows service configuration with WinSW
- Basic installer using Inno Setup

### Changed

- Updated package references to latest versions
