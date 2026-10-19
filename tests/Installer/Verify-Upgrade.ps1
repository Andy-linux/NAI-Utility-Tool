#Requires -Version 7.0
param (
    [Parameter(Mandatory = $true)][string]$Setup,
    [Parameter(Mandatory = $true)][string]$BaselineZip,
    [Parameter(Mandatory = $true)][string]$Payload,
    [Parameter(Mandatory = $true)][string]$TestRoot,
    [string]$OlderFork2 = ""
)
$ErrorActionPreference = "Stop"
$reg = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{68F68AD3-6E07-495D-98CD-CFC119CCB43D}_is1'
$shortcut = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\NAI Utility Tool.lnk"
# Registered tests must not displace an existing user's installation or shortcut.
if ((Test-Path $TestRoot) -or (Test-Path $reg) -or (Test-Path $shortcut)) {
    throw 'Use a fresh test directory and a Windows account without a registered NAI Utility Tool installation.'
}
New-Item -ItemType Directory -Path $TestRoot | Out-Null
& dotnet build "$PSScriptRoot\SettingsProbe.csproj" -c Release -o "$TestRoot\probe" *> "$TestRoot\probe-build.log"
if ($LASTEXITCODE -ne 0) { throw 'Settings probe build failed' }
$probe = "$TestRoot\probe\SettingsProbe.dll"
$expectedDll = (Get-FileHash "$Payload\bin\NAI Utility Tool.dll").Hash
$results = [Collections.Generic.List[object]]::new()
function Check([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}
function Invoke-Setup([string]$target, [bool]$portable, [string]$label, [bool]$success = $true) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$target`"", "/LOG=`"$TestRoot\$label.log`"", '/LANG=en', '/TASKS=""')
    if ($portable) { $arguments += '/PORTABLE=1' }
    $process = Start-Process -FilePath $Setup -ArgumentList $arguments -PassThru
    if (-not $process.WaitForExit(120000)) { throw "Installer did not finish: $label (PID $($process.Id))" }
    if ($success) { Check ($process.ExitCode -eq 0) "${label}: installer exit $($process.ExitCode)" }
    else { Check ($process.ExitCode -ne 0) "$label should reject this destination" }
    return $process.ExitCode
}
function Check-Payload([string]$target) {
    Check (Test-Path "$target\NAI Utility Tool.exe") 'Root launcher missing'
    Check ((Get-FileHash "$target\bin\NAI Utility Tool.dll").Hash -eq $expectedDll) 'The installed DLL differs from the released DLL'
}
function Data-Map([string]$target) {
    $map = @{}
    foreach ($name in 'user','output','logs','assets','models') {
        if (Test-Path "$target\$name") {
            foreach ($file in Get-ChildItem "$target\$name" -File -Recurse) {
                $map[[IO.Path]::GetRelativePath($target, $file.FullName)] = (Get-FileHash $file.FullName).Hash
            }
        }
    }
    return $map
}
function Check-Data($before, [string]$target) {
    foreach ($relative in $before.Keys) {
        $file = Join-Path $target $relative
        Check (Test-Path $file) "Data deleted: $relative"
        Check ((Get-FileHash $file).Hash -eq $before[$relative]) "Data overwritten: $relative"
    }
}
function Write-Fixture([string]$root, [string]$relative, [string]$value) {
    $file = Join-Path $root $relative
    New-Item -ItemType Directory -Force (Split-Path $file -Parent) | Out-Null
    [IO.File]::WriteAllText($file, $value)
}
function Probe-Settings([string]$mode, [string]$root) {
    $text = & dotnet $probe $mode $root
    if ($LASTEXITCODE -ne 0) { throw "Settings $mode failed" }
    return ($text | ConvertFrom-Json)
}
# Real fork.1 binaries write their own settings and encrypted account schema.
$upgrade = Join-Path $TestRoot 'fork1 升级 portable'
Expand-Archive -LiteralPath $BaselineZip -DestinationPath $upgrade
$oldProbe = Probe-Settings seed $upgrade
Write-Fixture $upgrade 'user\fxpresets\Battlefield.json' '{"fixture":"customized preset"}'
Write-Fixture $upgrade 'user\wildcards\AnimagineXLV3\character.txt' 'custom wildcard'
Write-Fixture $upgrade 'user\userprompts\installer-test.txt' 'saved prompt'
Write-Fixture $upgrade 'user\automation\presets\installer-test.json' '{"fixture":"automation"}'
Write-Fixture $upgrade 'user\vibe\installer-test.bin' 'vibe cache'
Write-Fixture $upgrade 'user\reference-drafts\installer-test.bin' 'reference draft'
Write-Fixture $upgrade 'output\2026-10\installer-test.txt' 'saved output'
Write-Fixture $upgrade 'logs\installer-test.log' 'retained log'
Write-Fixture $upgrade 'assets\wildcards\AnimagineXLV3\character.txt' 'customized bundled resource'
Write-Fixture $upgrade 'models\tagger\installer-test.onnx' 'user-downloaded model placeholder'
$before = Data-Map $upgrade
$null = Invoke-Setup $upgrade $true 'fork1-portable-upgrade'
Check-Payload $upgrade
Check-Data $before $upgrade
Check (-not (Test-Path $reg)) 'Portable upgrade registered an uninstall entry'
Check (-not (Test-Path $shortcut)) 'Portable upgrade created a shortcut'
Check (-not (Test-Path "$upgrade\uninstall")) 'Portable upgrade created an uninstaller'
$newProbe = Probe-Settings check $upgrade
$results.Add(@{scenario='fork.1 portable upgrade';passed=$true;preservedFiles=$before.Count;before=$oldProbe;after=$newProbe})

$null = Invoke-Setup $upgrade $true 'repeat-portable-upgrade'
Check-Payload $upgrade; Check-Data $before $upgrade
$results.Add(@{scenario='repeat same-version upgrade';passed=$true})

$lockedHash = (Get-FileHash "$upgrade\bin\NAI Utility Tool.dll").Hash
$lock = [IO.File]::Open("$upgrade\bin\NAI Utility Tool.dll", [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
try { $exit = Invoke-Setup $upgrade $true 'locked-target' $false }
finally { $lock.Dispose() }
Check ((Get-FileHash "$upgrade\bin\NAI Utility Tool.dll").Hash -eq $lockedHash) 'Locked target changed'
Check-Data $before $upgrade
$results.Add(@{scenario='locked target rejected before writes';passed=$true;exitCode=$exit})

$exit = Invoke-Setup "$upgrade\bin" $true 'wrong-bin-target' $false
Check (-not (Test-Path "$upgrade\bin\bin")) 'Nested bin directory was created'
$results.Add(@{scenario='wrong bin destination rejected';passed=$true;exitCode=$exit})

$fresh = Join-Path $TestRoot 'fresh 便携版'
$null = Invoke-Setup $fresh $true 'fresh-portable'
Check-Payload $fresh
Check (-not (Test-Path "$fresh\user")) 'Setup wrote a user directory'
Check (-not (Test-Path $reg)) 'Fresh portable installation changed registration'
$results.Add(@{scenario='fresh portable';passed=$true})

if ($OlderFork2) {
    $older = Join-Path $TestRoot 'older-fork2'
    Copy-Item -LiteralPath $OlderFork2 -Destination $older -Recurse
    $null = Probe-Settings seed $older
    $saved = Data-Map $older
    $null = Invoke-Setup $older $true 'older-fork2-upgrade'
    Check-Payload $older; Check-Data $saved $older
    $null = Probe-Settings check $older
    $results.Add(@{scenario='old fork.2 asset upgraded';passed=$true;preservedFiles=$saved.Count})
}

$installed = Join-Path $TestRoot 'fresh installed'
$null = Invoke-Setup $installed $false 'fresh-installed'
Check-Payload $installed
Check (Test-Path $reg) 'Installed mode has no uninstall entry'
Check (Test-Path $shortcut) 'Installed mode has no Start menu shortcut'
Check ([IO.Path]::GetFullPath((Get-ItemProperty $reg).InstallLocation).TrimEnd('\') -eq $installed) 'Uninstall entry points to another directory'
Write-Fixture $installed 'user\userprompts\keep.txt' 'keep after uninstall'
Write-Fixture $installed 'output\keep.txt' 'keep generated output'
$saved = Data-Map $installed
$null = Invoke-Setup $installed $false 'repeat-installed'
Check-Payload $installed; Check-Data $saved $installed
$results.Add(@{scenario='fresh installed and registered repeat upgrade';passed=$true})
$uninstaller = Start-Process -FilePath "$installed\uninstall\unins000.exe" -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/LOG=`"$TestRoot\uninstall.log`"" -Wait -PassThru
Check ($uninstaller.ExitCode -eq 0) 'Uninstall failed'
Check (-not (Test-Path $reg)) 'Uninstall entry remains'
Check (-not (Test-Path $shortcut)) 'Uninstall shortcut remains'
Check-Data $saved $installed
$results.Add(@{scenario='uninstall retains user data and resources';passed=$true;preservedFiles=$saved.Count})
$results | ConvertTo-Json -Depth 6 | Set-Content "$TestRoot\results.json" -Encoding utf8
$results | ConvertTo-Json -Depth 6
