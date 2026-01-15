# DICOM7

A comprehensive bidirectional DICOM and HL7 interface suite with multiple components:

- **DICOM2ORM**: Queries DICOM Modality Worklist and generates HL7 ORM orders
- **DICOM2ORU**: Processes DICOM images and generates HL7 ORU result messages
- **ORM2DICOM**: Receives HL7 ORM orders and creates DICOM Modality Worklist entries
- **ORU2DICOM**: Listens for HL7 ORU results, renders DICOM objects, and forwards them via C-STORE

## License

This software remains proprietary to Flux Inc. and may only be used as permitted under the **Medical Software Academic and Restricted Use License**. Any copying, transferring, or reproduction outside the scope of that license is prohibited.

The license provides:

- **Academic Users**: Free to download, use, and modify for non-commercial purposes with attribution.
- **Commercial Use**: Prohibited without written permission from Flux Inc.
- **Competitors**: Prohibited from use.
- **Clients**: Prohibited from use by clients of licensees.

See the [LICENSE](LICENSE.md) file for full terms.

## Features

### Common Features

- Template-based HL7 message generation with support for default templates
- YAML-based configuration
- Logging to console and file for easier troubleshooting
- Caching with configurable retention to prevent duplicate processing
- Automatic retry for failed operations

### DICOM2ORM

- Queries DICOM Modality Worklist (MWL) for scheduled procedures
- Generates HL7 ORM messages from DICOM worklist data
- Sends HL7 ORM messages to configured receivers

### DICOM2ORU

- Processes DICOM images to extract result data
- Generates HL7 ORU messages containing results
- Sends HL7 ORU messages to configured receivers

### ORM2DICOM

- Receives HL7 ORM messages via MLLP
- Creates DICOM Modality Worklist entries from received HL7 data
- Provides DICOM Worklist SCP functionality

### ORU2DICOM

- Receives HL7 ORU messages over MLLP, validates them, and persists the raw payloads for auditing
- Converts structured results (and optional PDF attachments) into DICOM instances ready for C-STORE
- Streams generated DICOM artifacts to a remote PACS using configurable AE titles, TLS, and retry logic
- Supports deferred acknowledgements and tunable retry policies so upstream systems see meaningful status

## Requirements

- .NET Framework 4.6.2
- Windows environment (not compatible with macOS/Linux)

## Why .NET Framework 4.6.2?

- **Windows 10 Compatibility**: Most use cases are for legacy medical software frequently installed on Windows 10, and this only supports .NET <= 4.6.2.

## Dependencies

- **fo-dicom**: DICOM implementation for .NET
- **Efferent.HL7.V2**: Lightweight HL7-V2 message handler
- **Serilog**: Logging framework
- **YamlDotNet**: YAML file parsing

## Configuration

Each component has its own `config.yaml` file with appropriate settings for that component's functionality. Refer to the documentation within each project directory for specific configuration options.

## Building and Running

### Local

```cmd
msbuild DICOM7.sln /p:Configuration=Release /p:Platform="Any CPU"
```

### Remote via Helper Script

```bash
./build.sh solution Debug
./build.sh solution Release --installer
```

The script uses the environment variables from `.envrc` (or your shell) to locate the Windows host, run MSBuild remotely, and optionally package the installer after a successful build. Each application is designed to run continuously as a service, operating according to its configured parameters.

When the target is `solution`, `all`, `rebuild`, or `orm2dicom`, the script automatically executes `ORM2DICOM.Tests` using the xUnit console runner (downloaded to `packages/` on-demand). Test results are collected under `TestResults/`.

# Remote Environment Setup

This repository uses `.envrc` to describe the remote Windows build host. Copy the template and adjust the paths for your VM:

- `cp .envrc.example .envrc`
- Update `SSH_HOST`, `REMOTE_PROJECT_DIR`, and `REMOTE_RUN_DIR` for your machine (defaults point to `windev` and `c:\dev\dicom7`)
- Run `direnv allow` so the variables are loaded when you `cd` into the repo

If you do not use Direnv, export the same variables manually before running the helper scripts.

# Remote Build Notes

Sync this solution to `%REMOTE_PROJECT_DIR%` on a Windows VM or remote workstation (via Syncthing—allow a short delay after local edits before building).

## Typical Workflow

- Preferred: run `./build.sh solution Debug` for a quick verification build and `./build.sh solution Release --installer` when you need the signed installer in `installer/Output`.

- Start an interactive session: `ssh "$SSH_HOST"`
- Clear stale locks if MSBuild warns about file access:  
  `for /d %d in (%REMOTE_PROJECT_DIR%\*\obj) do rd /s /q "%d"`
- From `%REMOTE_PROJECT_DIR%`, run solution builds (warnings only today):
  - `"%VS_MSBUILD%" DICOM7.sln /p:Configuration=Debug /p:Platform="Any CPU"`
  - `"%VS_MSBUILD%" DICOM7.sln /p:Configuration=Release /p:Platform="Any CPU"`
- Optional: target one project with `"%VS_MSBUILD%" DICOM2ORU\DICOM2ORU.csproj /p:Configuration=Debug`
- Package the installer when needed:  
  `"%INNO_SETUP_COMPILER%" installer\DICOM7Setup.iss`
- Outputs land under `%REMOTE_PROJECT_DIR%\{Project}\bin\<Config>`; installer artifacts appear in `installer\Output`
- Runtime assets (config, queue, logs) belong under `%REMOTE_RUN_DIR%`
- Automated tests: the helper script (and manual MSBuild flow when targeting `ORM2DICOM`) will run the xUnit suite in `ORM2DICOM.Tests`; detailed results are emitted to `TestResults\`.

## Command Line Arguments

Each application supports the following command line arguments:

- `--path <directory_path>`: Sets a custom base path for the application. All other paths (configuration, cache, logs) will be relative to this path unless explicitly configured otherwise in the config.yaml file.

Example:

```cmd
DICOM2ORM.exe --path C:\CustomPath\DICOM2ORM
```

## Testing

- Automated coverage currently focuses on `ORM2DICOM.Tests`, which exercises core order-to-DICOM workflows.
- Run tests manually on the remote Windows host with  
  `"%VS_MSBUILD%" ORM2DICOM.Tests\ORM2DICOM.Tests.csproj /p:Configuration=Debug` followed by  
  `packages\xunit.runner.console.2.1.0\tools\xunit.console.exe ORM2DICOM.Tests\bin\Debug\ORM2DICOM.Tests.dll`.
- For broader validation, replay sample HL7 payloads from `samples/newman-abi/` through the matching services and inspect the generated DICOM/HL7 traffic in `run/logs`.

## Support

For issues or questions, please create an issue, or contact Flux Inc. support at [support@fluxinc.co](mailto:support@fluxinc.co).
