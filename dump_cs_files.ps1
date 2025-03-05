# Script to concatenate all C# files into a single file for LLM context
$outputFile = "OrderORM_codebase.txt"

# Delete output file if it already exists
if (Test-Path $outputFile) {
    Remove-Item $outputFile
}

# Get all .cs files, excluding obj/ directory files
$files = Get-ChildItem -Path . -Filter "*.cs" -Recurse | Where-Object { $_.FullName -notlike "*\obj\*" }

# Write header to output file
"# OrderORM C# Codebase" | Out-File -FilePath $outputFile

foreach ($file in $files) {
    # Add file path as header
    "`n`n## File: $($file.FullName)" | Out-File -FilePath $outputFile -Append
    '```csharp' | Out-File -FilePath $outputFile -Append

    # Add file content
    Get-Content $file.FullName | Out-File -FilePath $outputFile -Append

    # Close code block
    '```' | Out-File -FilePath $outputFile -Append
}

Write-Output "Output written to $outputFile"
Write-Output "Found and processed $($files.Count) C# files."
