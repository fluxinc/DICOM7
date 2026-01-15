#!/usr/bin/env bash

# DICOM7 Remote Build Script
# Facilitates building and packaging the Windows solution from a UNIX-like host.
# Usage: ./build.sh [solution|clean|rebuild|dicom2orm|dicom2oru|orm2dicom|oru2dicom|shared] [Debug|Release] [--installer]

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Optional secondary config file (kept for backwards compatibility with older automation)
if [[ -f "${SCRIPT_DIR}/.build.env" ]]; then
  echo "Loading configuration from .build.env..."
  # shellcheck disable=SC1090
  source "${SCRIPT_DIR}/.build.env"
fi

# Defaults (can be overridden via environment variables or .envrc)
: "${SSH_HOST:=windev}"
: "${REMOTE_PROJECT_DIR:=c:\\dev\\dicom7}"
: "${REMOTE_RUN_DIR:=c:\\dev\\dicom7\\run}"
: "${VS_MSBUILD:=C:\\Program Files\\Microsoft Visual Studio\\2022\\Community\\MSBuild\\Current\\Bin\\MSBuild.exe}"
: "${INNO_SETUP_COMPILER:=C:\\ProgramData\\chocolatey\\bin\\ISCC.exe}"
: "${VS_VSTEST:=C:\\Program Files\\Microsoft Visual Studio\\2022\\Community\\Common7\\IDE\\Extensions\\TestPlatform\\vstest.console.exe}"
: "${NUGET_EXE:=C:\\Program Files (x86)\\Microsoft Visual Studio\\2022\\Community\\Common7\\IDE\\CommonExtensions\\Microsoft\\NuGet\\NuGet.exe}"
: "${DEFAULT_CONFIG:=Debug}"

BUILD_TARGET="solution"
CONFIG="${DEFAULT_CONFIG}"
BUILD_INSTALLER=false

lower() {
  echo "${1,,}"
}

usage() {
  cat <<'EOF'
Usage: ./build.sh [target] [Debug|Release] [--installer]

Targets:
  solution|all   Build the full DICOM7.sln (default)
  clean          Clean all build outputs
  rebuild        Clean and rebuild the solution
  dicom2orm      Build the DICOM2ORM project only
  dicom2oru      Build the DICOM2ORU project only
  orm2dicom      Build the ORM2DICOM project only
  oru2dicom      Build the ORU2DICOM project only
  shared         Build the Shared project only

Options:
  Debug|Release  Override the build configuration (default from DEFAULT_CONFIG)
  --installer    After a successful build, run the Inno Setup compiler
EOF
}

while [[ $# -gt 0 ]]; do
  case "$(lower "$1")" in
    solution|all|clean|rebuild|dicom2orm|dicom2oru|orm2dicom|oru2dicom|shared)
      BUILD_TARGET="$(lower "$1")"
      shift
      ;;
    debug|release)
      tmp="$(lower "$1")"
      CONFIG="${tmp^}"
      shift
      ;;
    --installer|-i)
      BUILD_INSTALLER=true
      shift
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1"
      usage
      exit 1
      ;;
  esac
done

echo "DICOM7 Remote Build"
echo "==================="
echo "SSH Host            : ${SSH_HOST}"
echo "Remote Project Dir  : ${REMOTE_PROJECT_DIR}"
echo "Remote Run Dir      : ${REMOTE_RUN_DIR}"
echo "Configuration       : ${CONFIG}"
echo "Target              : ${BUILD_TARGET}"
echo "Build Installer     : ${BUILD_INSTALLER}"
echo

remote_exec() {
  local cmd="$1"
  echo "Executing: ssh ${SSH_HOST} \"${cmd}\""
  if ! ssh "${SSH_HOST}" "${cmd}"; then
    echo "ERROR: Remote command failed: ${cmd}"
    exit 1
  fi
}

echo "Verifying SSH connectivity..."
remote_exec "echo Remote host reachable"
echo

echo "Checking remote project directory..."
remote_exec "if not exist \"${REMOTE_PROJECT_DIR}\" (echo Missing directory && exit 1) else echo Directory OK"
echo

msbuild_solution() {
  local verb="$1" # Build / Clean / Rebuild
  remote_exec "cd \"${REMOTE_PROJECT_DIR}\" && \"${VS_MSBUILD}\" DICOM7.sln /t:${verb} /p:Configuration=${CONFIG} /p:Platform=\"Any CPU\" /v:m /nologo /restore"
}

msbuild_project() {
  local project_path="$1"
  remote_exec "cd \"${REMOTE_PROJECT_DIR}\" && \"${VS_MSBUILD}\" \"${project_path}\" /p:Configuration=${CONFIG} /p:Platform=\"Any CPU\" /v:m /nologo /restore"
}

run_tests() {
  local test_results_dir="TestResults"
  local runner_path="packages\\xunit.runner.console.2.1.0\\tools\\xunit.console.exe"

  echo
  echo "Setting up test environment..."

  remote_exec "cd \"${REMOTE_PROJECT_DIR}\" && if not exist \"${test_results_dir}\" mkdir \"${test_results_dir}\""
  remote_exec "cd \"${REMOTE_PROJECT_DIR}\" && if not exist \"${runner_path}\" (\"${NUGET_EXE}\" install xunit.runner.console -Version 2.1.0 -OutputDirectory packages)"
  remote_exec "cd \"${REMOTE_PROJECT_DIR}\" && if not exist \"${runner_path}\" (echo Missing xUnit runner: ${runner_path} & exit 1)"

  # Run ORM2DICOM.Tests
  local orm2dicom_dll="ORM2DICOM.Tests\\bin\\${CONFIG}\\ORM2DICOM.Tests.dll"
  echo
  echo "Running ORM2DICOM.Tests..."
  remote_exec "cd \"${REMOTE_PROJECT_DIR}\" && if not exist \"${orm2dicom_dll}\" (echo Test assembly not found: ${orm2dicom_dll} & exit 1)"
  remote_exec "cd \"${REMOTE_PROJECT_DIR}\" && \"${runner_path}\" \"${orm2dicom_dll}\" -xml \"${test_results_dir}\\ORM2DICOM_${CONFIG}.xml\""

  # Run DICOM2ORM.Tests
  local dicom2orm_dll="DICOM2ORM.Tests\\bin\\${CONFIG}\\DICOM2ORM.Tests.dll"
  echo
  echo "Running DICOM2ORM.Tests..."
  remote_exec "cd \"${REMOTE_PROJECT_DIR}\" && if not exist \"${dicom2orm_dll}\" (echo Test assembly not found: ${dicom2orm_dll} & exit 1)"
  remote_exec "cd \"${REMOTE_PROJECT_DIR}\" && \"${runner_path}\" \"${dicom2orm_dll}\" -xml \"${test_results_dir}\\DICOM2ORM_${CONFIG}.xml\""
}

case "${BUILD_TARGET}" in
  solution|all)
    echo "Building full solution..."
    msbuild_solution Build
    ;;
  clean)
    echo "Cleaning solution outputs..."
    msbuild_solution Clean
    ;;
  rebuild)
    echo "Rebuilding solution..."
    msbuild_solution Rebuild
    ;;
  dicom2orm)
    echo "Building DICOM2ORM..."
    msbuild_project "DICOM2ORM\\DICOM2ORM.csproj"
    ;;
  dicom2oru)
    echo "Building DICOM2ORU..."
    msbuild_project "DICOM2ORU\\DICOM2ORU.csproj"
    ;;
  orm2dicom)
    echo "Building ORM2DICOM..."
    msbuild_project "ORM2DICOM\\ORM2DICOM.csproj"
    ;;
  oru2dicom)
    echo "Building ORU2DICOM..."
    msbuild_project "ORU2DICOM\\ORU2DICOM.csproj"
    ;;
  shared)
    echo "Building Shared library..."
    msbuild_project "Shared\\Shared.csproj"
    ;;
  *)
    echo "Unsupported build target: ${BUILD_TARGET}"
    usage
    exit 1
    ;;
esac

echo
echo "Build step completed successfully."

should_run_tests=false
case "${BUILD_TARGET}" in
  solution|all|rebuild|orm2dicom|dicom2orm)
    should_run_tests=true
    ;;
esac

if [[ "${should_run_tests}" == true ]]; then
  run_tests
fi

if [[ "${BUILD_INSTALLER}" == true ]]; then
  echo
  echo "Building installer with Inno Setup..."
  remote_exec "cd \"${REMOTE_PROJECT_DIR}\" && \"${INNO_SETUP_COMPILER}\" installer\\DICOM7Setup.iss"
  echo "Installer build completed."
fi

echo
echo "All tasks finished successfully."
