# OrderORM - C# .NET Development Guidelines

## Build & Run Commands
- ⚠️ WARNING: This is a .NET 4.7.2 project that doesn't run on the current OS
- Build: `msbuild`
- Run: `bin\Debug\OrderORM.exe`
- Run with custom base path: `bin\Debug\OrderORM.exe --path .\`

## Project Structure
- Medical order management system that:
  - Queries DICOM worklists
  - Generates HL7 ORM messages
  - Implements caching with configurable retention

## Code Style Guidelines
- Naming: PascalCase for classes/methods, camelCase for variables
- Use explicit types instead of var when possible
- Prefer async/await over direct Task management
- Handle exceptions with try/catch blocks, log details
- XML documentation for public methods/classes
- Organize imports alphabetically
- Configuration via YAML (config.yaml)

## Additional Notes
- Uses fo-dicom, NHapi, and YamlDotNet libraries
- Configuration stored in config.yaml
- Command line arguments:
  - `--path <directory_path>`: Sets a custom base path for the application
