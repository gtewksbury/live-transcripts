[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot "src\LiveTranscripts\LiveTranscripts.csproj"
$artifactPath = Join-Path $repositoryRoot "artifacts\windows-x64"

if (Test-Path $artifactPath) {
    Remove-Item $artifactPath -Recurse -Force
}

& dotnet publish $projectPath --configuration Release --property:PublishProfile=WindowsX64 --nologo
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Output "Published live-transcripts.exe to $artifactPath"