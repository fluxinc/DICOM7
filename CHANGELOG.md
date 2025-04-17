# Changelog

All notable changes to the DICOM2ORM project will be documented in this file.

## [Unreleased]


## Changed

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
