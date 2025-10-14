# Repository Guidelines

## Project Structure & Module Organization
- `DICOM2ORM`, `DICOM2ORU`, and `ORM2DICOM` are the three services, each owning its `config.yaml`, `run/` runtime assets, and `bin/` output.
- `Shared` hosts reusable config, caching, and HL7/DICOM utilities compiled into `DICOM7.Shared`.
- `flux-net` carries networking, licensing, and job-system helpers; `installer` and `packages` store deployment artifacts.
- `samples/newman-abi` provides ORM/ORU message fixtures for manual validation.

## Build, Test, and Development Commands
- Configure Direnv: `cp .envrc.example .envrc`, adjust `SSH_HOST`, `REMOTE_PROJECT_DIR`, and `REMOTE_RUN_DIR`, then run `direnv allow`.
- Build the full suite locally with `msbuild DICOM7.sln /p:Configuration=Release /p:Platform="Any CPU"`; outputs land under each project's `bin/<Config>`.
- Clean before a fresh build via `msbuild DICOM7.sln /t:Clean`.
- Focus on one service using `msbuild DICOM2ORM/DICOM2ORM.csproj /p:Configuration=Debug`.
- Start services on Windows with `DICOM2ORM\bin\Debug\DICOM2ORM.exe --path C:\Flux\DICOM2ORM` (or equivalent) to control cache and log roots.

## Remote Build Workflow

### Automated Sync & Build

- Sync this workspace with your Windows VM using Syncthing; allow a few seconds for changes to propagate.
- Run `./build.sh solution Debug` for a quick smoke build or `./build.sh solution Release --installer` when you need an installable package. The script handles SSH connectivity, path validation, MSBuild invocation, and optional Inno Setup packaging.

### Manual Steps

- Connect with `ssh "$SSH_HOST"` (default `windev`) and let Syncthing complete any pending sync.
- Work from `%REMOTE_PROJECT_DIR%` (default `c:\dev\dicom7`); clear build locks with `for /d %d in (%REMOTE_PROJECT_DIR%\*\obj) do rd /s /q "%d"`.
- Build via `"%VS_MSBUILD%" DICOM7.sln /p:Configuration=Debug /p:Platform="Any CPU"` and rerun with `Configuration=Release` when needed.
- Package with `"%INNO_SETUP_COMPILER%" installer\DICOM7Setup.iss`; installers land in `installer\Output`, while binaries run from `%REMOTE_PROJECT_DIR%\{Project}\bin\<Config>` using `%REMOTE_RUN_DIR%` for configs, cache, and logs.

## Coding Style & Naming Conventions
- Honor `.editorconfig`: 2-space indent, spaces (no tabs), CRLF endings, final newline.
- Favor single-line braces when legal and stick with explicit types over target-typed `new`.
- Classes/methods/public members are PascalCase, private fields `_camelCase`, constants `ALL_CAPS_WITH_UNDERSCORES`.
- Log with Serilog templates and keep exception context intact before rethrowing or propagating.

## Testing Guidelines
- No automated suite exists; replay `samples/newman-abi` HL7 files through the matching service for smoke coverage.
- Verify staging connectivity with the endpoints in `config.yaml` and check `run/logs` for retries or cache hits.
- When adjusting parsing or templates, capture before/after HL7 payloads and share diffs with the review.

## Commit & Pull Request Guidelines
- Mirror existing history: concise, imperative subjects (e.g. `Refine HL7 template loading`) with extra detail in the body.
- Keep commits scoped and call out touched services (`DICOM2ORU`, `Shared`) to guide reviewers.
- PRs need intent, config notes, validation evidence (commands run, sample outputs), and linked tickets when available.
- Ensure installers/config artifacts stay aligned and involve the module owners for review.
