param(
    [string]$BuildDirectory = "",
    [string]$ContractDirectory = "",
    [string]$FixtureDirectory = "",
    [string]$Dotnet = "dotnet",
    [string]$LogDirectory = "",
    [string]$RestoreSource = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if (!$BuildDirectory) { $BuildDirectory = Join-Path $root "build/ParityV5Core/bin" }
if (!$ContractDirectory) { $ContractDirectory = Join-Path $root "../NAI-Behavior-Reference/system/20261126/contracts" }
if (!$FixtureDirectory) { $FixtureDirectory = Join-Path $root "../.runs/restart-20261126/fixtures/reference-oracle" }
$bin = (Resolve-Path $BuildDirectory).Path
$contracts = (Resolve-Path $ContractDirectory).Path
$fixtures = (Resolve-Path $FixtureDirectory).Path
if ($LogDirectory) { New-Item -ItemType Directory -Force $LogDirectory | Out-Null; $LogDirectory = (Resolve-Path $LogDirectory).Path }
$results = [System.Collections.Generic.List[object]]::new()
function Run-Test([string]$Name, [string]$Project, [string[]]$Arguments = @(), [switch]$HostAssembly) {
    $parameters = @("run", "--project", (Join-Path $root $Project), "-c", "Release", "-p:NuGetAudit=false")
    if ($RestoreSource) { $parameters += "-p:RestoreSources=$RestoreSource" }
    if ($HostAssembly) { $parameters += "-p:NaitoolBuildDirectory=$($bin.Replace('\','/'))" }
    $parameters += "--"
    $parameters += $Arguments
    Write-Host "=== $Name ==="
    $output = & $Dotnet @parameters 2>&1
    $code = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    if ($LogDirectory) { $output | Set-Content (Join-Path $LogDirectory "$Name.log") -Encoding utf8 }
    $count = 0
    foreach ($line in $output) {
        if ([string]$line -match '(?i)\bPassed\s+(\d+)\s+' -or [string]$line -match '(?i)\b(\d+)\s+(?:behavior assertions|checks)\s+passed\b') {
            $count += [int]$Matches[1]
        }
    }
    $results.Add([pscustomobject]@{Suite=$Name;Passed=$count;ExitCode=$code})
}
Push-Location $root
$oldPath = $env:PATH
try {
    $env:PATH = "$bin;$oldPath"
    foreach ($name in @("AnlasPricing","ApiAccounts","History","HistoryThumbnails","MaskEditing","PreviewPan","Updates")) {
        Run-Test $name "tests/$name/$name.csproj"
    }
    foreach ($name in @("ApiTransport","Enhance","LocalUpscale","Seeds","Upscale")) {
        Run-Test $name "tests/$name/$name.csproj" -Arguments @($bin) -HostAssembly
    }
    Run-Test "ApiTransport-V5" "tests/ApiTransport/ApiTransport.csproj" -Arguments @($bin,"--v5") -HostAssembly
    foreach ($suite in @("models","pricing","references","metadata","imaging","pricing-reference")) {
        Run-Test "ParityCore-$suite" "tests/ParityCore/ParityCore.csproj" -Arguments @($suite,$contracts,$fixtures)
    }
} finally {
    $env:PATH = $oldPath
    Pop-Location
}
$results | Format-Table -AutoSize
if ($LogDirectory) { $results | ConvertTo-Json | Set-Content (Join-Path $LogDirectory "results.json") -Encoding utf8 }
$failed = @($results | Where-Object ExitCode -ne 0)
Write-Host "Suites passed: $($results.Count-$failed.Count)/$($results.Count); failed: $($failed.Count)"
if ($failed.Count) { exit 1 }
exit 0
