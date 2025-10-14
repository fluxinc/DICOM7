param(
  [Parameter(Mandatory = $false)]
  [ValidateSet('major', 'minor', 'patch')]
  [string]$Bump,

  [Parameter(Mandatory = $false)]
  [string]$Version
)

if (-not $Version -and -not $Bump) {
  throw "Specify -Bump (major|minor|patch) or provide an explicit -Version."
}

if ($Version -and $Bump) {
  Write-Host "Ignoring -Bump because -Version was provided."
}

$scriptRoot = $PSScriptRoot
if (-not $scriptRoot) {
  $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}
$projectRoot = Split-Path -Parent $scriptRoot

$installerPath = Join-Path $projectRoot "installer/DICOM7Setup.iss"
if (-not (Test-Path $installerPath)) {
  throw "Installer definition not found at $installerPath."
}

function Parse-Version {
  param([string]$Value)

  $pattern = '^(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?(?:\.(?<revision>\d+))?$'
  $match = [regex]::Match($Value, $pattern)
  if (-not $match.Success) {
    throw "Invalid version format: $Value"
  }

  $patch = if ($match.Groups['patch'].Success) { [int]$match.Groups['patch'].Value } else { 0 }
  $revision = if ($match.Groups['revision'].Success) { [int]$match.Groups['revision'].Value } else { 0 }

  return [pscustomobject]@{
    Major        = [int]$match.Groups['major'].Value
    Minor        = [int]$match.Groups['minor'].Value
    Patch        = $patch
    Revision     = $revision
    OriginalText = $Value
  }
}

$currentVersionMatch = Select-String -Path $installerPath -Pattern '#define\s+MyAppVersion\s+"(?<version>[\d\.]+)"'
if (-not $currentVersionMatch) {
  throw "Unable to locate MyAppVersion in $installerPath."
}
$currentVersion = Parse-Version $currentVersionMatch.Matches[0].Groups['version'].Value

if ($Version) {
  $targetVersion = Parse-Version $Version
} else {
  $major = $currentVersion.Major
  $minor = $currentVersion.Minor
  $patch = $currentVersion.Patch

  switch ($Bump) {
    'major' { $major++; $minor = 0; $patch = 0 }
    'minor' { $minor++; $patch = 0 }
    'patch' { $patch++ }
    default { throw "Unsupported bump type: $Bump" }
  }

  $targetVersion = [pscustomobject]@{
    Major    = $major
    Minor    = $minor
    Patch    = $patch
    Revision = 0
  }
}

$productVersion = "{0}.{1}.{2}" -f $targetVersion.Major, $targetVersion.Minor, $targetVersion.Patch
$assemblyVersion = "{0}.{1}.{2}.{3}" -f $targetVersion.Major, $targetVersion.Minor, $targetVersion.Patch, $targetVersion.Revision

$assemblyFiles = @(
  "DICOM2ORM/Properties/AssemblyInfo.cs",
  "DICOM2ORU/Properties/AssemblyInfo.cs",
  "ORM2DICOM/Properties/AssemblyInfo.cs",
  "ORU2DICOM/Properties/AssemblyInfo.cs"
) | ForEach-Object { Join-Path $projectRoot $_ }

foreach ($assemblyFile in $assemblyFiles) {
  if (-not (Test-Path $assemblyFile)) {
    throw "AssemblyInfo not found at $assemblyFile."
  }

  $content = Get-Content -Path $assemblyFile -Raw
  $content = [regex]::Replace($content, '\[assembly:\s*AssemblyVersion\("[^"]*"\)\]', "[assembly: AssemblyVersion(`"$assemblyVersion`")]")
  $content = [regex]::Replace($content, '\[assembly:\s*AssemblyFileVersion\("[^"]*"\)\]', "[assembly: AssemblyFileVersion(`"$assemblyVersion`")]")
  Set-Content -Path $assemblyFile -Value $content -Encoding UTF8

  $relativePath = $assemblyFile.Replace($projectRoot + [System.IO.Path]::DirectorySeparatorChar, "")
  Write-Host "Updated $relativePath"
}

$installerContent = Get-Content -Path $installerPath -Raw
$installerContent = [regex]::Replace($installerContent, '#define\s+MyAppVersion\s+"[^"]+"', "#define MyAppVersion `"$productVersion`"")
Set-Content -Path $installerPath -Value $installerContent -Encoding UTF8
Write-Host "Updated installer/DICOM7Setup.iss"

$fromVersion = "{0}.{1}.{2}" -f $currentVersion.Major, $currentVersion.Minor, $currentVersion.Patch
Write-Host "Version bumped from $fromVersion to $productVersion (assembly: $assemblyVersion)"
