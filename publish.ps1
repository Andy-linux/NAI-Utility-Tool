param (
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    # Never overwrite an existing portable installation (including its user data).
    [string]$Destination = ""
)

$ErrorActionPreference = "Stop"

$RootDir      = $PSScriptRoot
$SrcDir       = Join-Path $RootDir "src\NAITool"
$LauncherDir  = Join-Path $RootDir "src\NAIToolLauncher"
$AppFileBase  = "NAI Utility Tool"
$PublishDir   = if ($Destination) { [IO.Path]::GetFullPath($Destination) } else {
    Join-Path $RootDir "publish\candidate-$(Get-Date -Format 'yyyyMMdd-HHmmss')\$AppFileBase"
}
$BinDir       = Join-Path $PublishDir "bin"

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($args -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Remove-PublishBloat {
    param (
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path $Path)) {
        return
    }

    $patterns = @(
        "*.pdb",
        "*.mdb",
        "*.dbg",
        "*.lib",
        "DirectML.Debug.dll",
        "createdump.exe",
        "mscordaccore.dll",
        "mscordaccore_*.dll",
        "mscordbi.dll"
    )

    foreach ($pattern in $patterns) {
        Get-ChildItem -LiteralPath $Path -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue |
            Remove-Item -Force
    }
}

# A release is built into a fresh destination, not into the user's installation.
if (Test-Path -LiteralPath $PublishDir) { throw "Destination already exists; choose a fresh directory." }
$parent = Split-Path $PublishDir -Parent
while ($parent) {
    if ((Test-Path -LiteralPath $parent) -and ((Get-Item -LiteralPath $parent).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Publish destination must not traverse a junction/symlink."
    }
    $parent = Split-Path $parent -Parent
}
$TrackedResources = @(& git -C $RootDir ls-files -- assets models)
if ($LASTEXITCODE -ne 0 -or $TrackedResources.Count -eq 0) { throw "Cannot verify tracked release resources." }
# Reject tracked resource junctions before any compiler wildcard expansion.
foreach ($relative in $TrackedResources) {
    $check = Get-Item -LiteralPath (Join-Path $RootDir $relative)
    while ($check.FullName -ne $RootDir) {
        if ($check.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse-point resource blocked: $relative" }
        $check = if ($check -is [IO.DirectoryInfo]) { $check.Parent } else { $check.Directory }
        if ($null -eq $check) { throw 'Resource escaped repository.' }
    }
}
# Embedded resource wildcards must not ingest local/untracked images or translations.
foreach ($file in Get-ChildItem (Join-Path $RootDir 'assets') -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($RootDir, $file.FullName).Replace('\', '/')
    if ($relative -match '^assets/(i18n|splash|svg)/' -or $relative -match '^assets/img/MaidAeka.*\.png$') {
        if ($relative -notin $TrackedResources) { throw "Untracked embedded resource blocked: $relative" }
    }
}
function Copy-TrackedResource([string]$Relative, [string]$TargetRelative) {
    $source = Join-Path $RootDir $Relative
    $check = Get-Item -LiteralPath $source
    while ($check.FullName -ne $RootDir) {
        if ($check.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse-point resource blocked: $Relative" }
        $check = if ($check -is [IO.DirectoryInfo]) { $check.Parent } else { $check.Directory }
        if ($null -eq $check) { throw "Resource escaped repository." }
    }
    $target = Join-Path $PublishDir $TargetRelative
    New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
}
# Git identifies source history; file checksums belong to the distributed binaries.
$SourceSha = (& git -C $RootDir rev-parse HEAD).Trim()
$SourceTree = (& git -C $RootDir rev-parse 'HEAD^{tree}').Trim()
$SourceDirty = @(& git -C $RootDir diff --name-only HEAD -- src assets models installer publish.ps1 Directory.Build.props global.json LICENSE).Count -gt 0
New-Item -ItemType Directory -Force $PublishDir | Out-Null
New-Item -ItemType Directory -Force $BinDir | Out-Null

Write-Host "正在发布主程序..." -ForegroundColor Green
Invoke-Dotnet publish "$SrcDir\NAITool.csproj" -c $Configuration -r $Runtime --self-contained true '-p:Platform=x64' '-p:PublishSingleFile=false' '-p:DebugType=None' '-p:DebugSymbols=false' -o "$BinDir"

Write-Host "正在发布启动器..." -ForegroundColor Green
Invoke-Dotnet publish "$LauncherDir\NAIToolLauncher.csproj" -c $Configuration -r $Runtime --self-contained true '-p:Platform=x64' '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=None' '-p:DebugSymbols=false' -o "$PublishDir"

# ── 从源仓库直接复制数据文件到发布根目录 ──
Write-Host "复制数据文件..." -ForegroundColor Green

$AssetsDir = Join-Path $PublishDir "assets"
New-Item -ItemType Directory -Force (Join-Path $AssetsDir "tagsheet") | Out-Null
New-Item -ItemType Directory -Force (Join-Path $AssetsDir "wildcards") | Out-Null

foreach ($relative in $TrackedResources) {
    if ($relative -match '^assets/(tagsheet|wildcards|fxpresets)/' -or
        ($relative -match '^assets/img/.*\.png$' -and $relative -notmatch '/MaidAeka')) {
        Copy-TrackedResource $relative $relative
    }
}

$ModelsUpscalerDir = Join-Path $PublishDir "models\upscaler"
New-Item -ItemType Directory -Force $ModelsUpscalerDir | Out-Null
foreach ($relative in $TrackedResources | Where-Object { $_ -match '^models/upscaler/.*\.onnx$' }) {
    Copy-TrackedResource $relative $relative
}

# Never ship user/, output/ or logs/: an archive overlay must not replace user data.
# Startup already copies missing presets/wildcards from assets/ without overwriting edits.

# Original GPL-3.0 license distributed with the binary.
Copy-Item -LiteralPath (Join-Path $RootDir "LICENSE") -Destination (Join-Path $PublishDir "LICENSE")
Copy-Item -LiteralPath (Join-Path $RootDir "installer\INSTALL.txt") -Destination (Join-Path $PublishDir "INSTALL.txt")

# ── 清理不需要的文件 ──
Remove-PublishBloat -Path $PublishDir

@{ sourceSha=$SourceSha; sourceTree=$SourceTree; sourceDirty=$SourceDirty; configuration=$Configuration; runtime=$Runtime; selfContained=$true; launcherSelfContained=$true; utc=[DateTime]::UtcNow.ToString('o') } |
    ConvertTo-Json | Set-Content -LiteralPath "$PublishDir.source.json" -Encoding utf8
Write-Host "发布完成！文件位于: $PublishDir" -ForegroundColor Green
