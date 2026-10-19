#Requires -Version 7.0
param (
    [Parameter(Mandatory = $true)][string]$PortableDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$Compiler = "ISCC.exe"
)

$ErrorActionPreference = "Stop"
$payload = (Resolve-Path -LiteralPath $PortableDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
[xml]$project = Get-Content -LiteralPath "$PSScriptRoot\src\NAITool\NAITool.csproj" -Raw
$version = [string]$project.Project.PropertyGroup.InformationalVersion
$fileVersion = [string]$project.Project.PropertyGroup.FileVersion

# The installer consumes the same clean payload as ZIP/7z, never a live installation.
foreach ($directory in 'user', 'output', 'logs') {
    if (Test-Path -LiteralPath (Join-Path $payload $directory)) {
        throw "Use a fresh publish.ps1 output: $directory must not be bundled."
    }
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
& $Compiler /Qp "/DReleaseDir=$payload" "/DAppVersion=$version" "/DAppFileVersion=$fileVersion" "/O$output" "$PSScriptRoot\installer\NAITool.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE" }
Write-Host "Installer: $(Join-Path $output "NAI.Utility.Tool.$version.Setup.exe")"
