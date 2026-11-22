<#
.SYNOPSIS
  Builds everything into dist\:
    SWH2-ModPack-<ver>\        ready folder: its contents go straight into the game folder
                               (SWH2ModLoader\ + Mods\<all mods>\ + README.txt guide)
    SWH2-ModPack-<ver>.zip     the same folder as one archive
    nexus\                     separate archives for Nexus Mods:
      SWH2ModLoader-<ver>.zip    unpacks to <game>\SWH2ModLoader\
      Randomizer-<ver>.zip       unpacks to <game>\Mods\Randomizer\   (same for Turbo, Challenges, CheatMenu)
  Every archive has the game folder as its root, so it works with manual "extract here" and with Vortex.
  Everything targets .NET Framework 4.8 (part of Windows 10 1903+ / 11): players install nothing else.

  -Deploy also copies SWH2ModLoader\ and Mods\ into the game folder (only adds/updates the loader and the mods;
  Backup\, profile.json and Reports\ are kept, game files are not touched until you click Apply in the loader).

  Unchanged parts are not rebuilt: the loader and each mod are cached in obj\package\<name>\ together with a hash of
  their sources (project folder, SWH2.Core, Directory.Build.props files). Same hash = reuse, dotnet is not run.
  -Rebuild ignores the cache.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\package.ps1
  powershell -ExecutionPolicy Bypass -File tools\package.ps1 -Deploy
  powershell -ExecutionPolicy Bypass -File tools\package.ps1 -Deploy -Game "D:\Games\SteamWorld Heist 2"
#>
param(
    [switch]$Deploy,
    [switch]$Rebuild,
    [string]$Game = 'C:\Program Files (x86)\Steam\steamapps\common\SteamWorld Heist 2'
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$cache = Join-Path $root 'obj\package'
$mods = 'Randomizer', 'Turbo', 'Challenges', 'CheatMenu', 'MoreSlots'
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Force (Join-Path $dist 'nexus') | Out-Null

function Invoke-Dotnet([string[]]$dotnetArgs) {
    & dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($dotnetArgs -join ' ') failed" }
}

# Hash of every source file (path + content) under the given folders/files, bin\ and obj\ excluded.
function Get-SourceHash([string[]]$paths) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $lines = foreach ($p in $paths) {
        $full = Join-Path $root $p
        $files = if (Test-Path $full -PathType Leaf) { Get-Item $full } else { Get-ChildItem $full -Recurse -File }
        foreach ($f in $files | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) {
            $h = [BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($f.FullName)))
            "$($f.FullName.Substring($root.Length)) $h"
        }
    }
    $text = ($lines | Sort-Object) -join "`n"
    [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))).Replace('-', '')
}

# Returns obj\package\<name>\out, rebuilding it with $build (which fills the folder passed to it) only when the
# sources changed. The hash is written last, so an interrupted build is never reused.
function Get-Cached([string]$name, [string[]]$sources, [scriptblock]$build) {
    $dir = Join-Path $cache $name
    $out = Join-Path $dir 'out'
    $hashFile = Join-Path $dir 'hash.txt'
    $hash = Get-SourceHash $sources
    if (-not $Rebuild -and (Test-Path $out) -and (Test-Path $hashFile) -and (Get-Content $hashFile -Raw).Trim() -eq $hash) {
        Write-Host "$name - unchanged, skipped"
        return $out
    }
    Write-Host "Building $name..."
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    New-Item -ItemType Directory -Force $out | Out-Null
    & $build $out | Out-Host   # keep dotnet's log out of the return value
    Set-Content $hashFile $hash
    $out
}

$common = 'SWH2.Core', 'Directory.Build.props'

function New-Zip([string]$zip, [string]$contentRoot) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    # Not ZipFile.CreateFromDirectory: on Windows PowerShell it stores "\" separators, which breaks other unzippers and Vortex.
    $archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $base = (Resolve-Path $contentRoot).Path.TrimEnd('\') + '\'
        foreach ($f in Get-ChildItem $contentRoot -Recurse -File) {
            $rel = $f.FullName.Substring($base.Length).Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $archive.Dispose() }
    Write-Host ("  {0}  ({1:N0} KB)" -f $zip.Substring($dist.Length + 1), ((Get-Item $zip).Length / 1KB))
}

# ---- loader -------------------------------------------------------------------------------
[xml]$core = Get-Content (Join-Path $root 'SWH2.Core\SWH2.Core.csproj')
$loaderVersion = $core.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$loaderSources = $common + 'SWH2.ModLoader', 'SWH2.Cli', 'docs\NEXUS_LOADER.md', 'docs\MAKING_MODS.md'
# Staged like the game folder: SWH2ModLoader\..., Mods\PUT_MODS_HERE.txt
$loaderStage = Get-Cached 'SWH2ModLoader' $loaderSources {
    param($o)
    $loaderDir = Join-Path $o 'SWH2ModLoader'
    $publish = @('-c', 'Release', '-o', $loaderDir, '-p:DebugType=none', '-p:GenerateDocumentationFile=false')
    Invoke-Dotnet (@('publish', (Join-Path $root 'SWH2.ModLoader\SWH2.ModLoader.csproj')) + $publish)
    Invoke-Dotnet (@('publish', (Join-Path $root 'SWH2.Cli\SWH2.Cli.csproj')) + $publish)
    Copy-Item (Join-Path $root 'docs\NEXUS_LOADER.md') (Join-Path $loaderDir 'README.md')
    Copy-Item (Join-Path $root 'docs\MAKING_MODS.md') (Join-Path $loaderDir 'MAKING_MODS.md')
    New-Item -ItemType Directory -Force (Join-Path $o 'Mods') | Out-Null
    Set-Content -Encoding UTF8 (Join-Path $o 'Mods\PUT_MODS_HERE.txt') 'Each mod goes in its own folder here: Mods\<ModId>\mod.json'
}

# ---- mods ---------------------------------------------------------------------------------
$modArchives = @()
$modStages = @{}
foreach ($mod in $mods) {
    # Staged like the game folder: Mods\<mod>\...
    $modStage = Get-Cached $mod ($common + "Mods\$mod", 'Mods\Directory.Build.props') {
        param($o)
        Invoke-Dotnet @('build', (Join-Path $root "Mods\$mod\$mod.csproj"), '-c', 'Release')
        $modDir = Join-Path $o "Mods\$mod"
        New-Item -ItemType Directory -Force $modDir | Out-Null
        Copy-Item (Join-Path $root "Mods\$mod\bin\Release\$mod\*") $modDir -Recurse
        if (Test-Path (Join-Path $modDir 'SWH2.Core.dll')) { throw "$mod output contains SWH2.Core.dll; the loader must provide it" }
    }
    $manifest = Get-Content (Join-Path $modStage "Mods\$mod\mod.json") -Raw | ConvertFrom-Json
    $modStages[$mod] = $modStage
    $modArchives += , @("$mod-$($manifest.version).zip", $modStage)
}

# ---- all-in-one pack: loader + every mod + guide, laid out like the game folder ------------
$packName = "SWH2-ModPack-$loaderVersion"
$pack = Join-Path $dist $packName
New-Item -ItemType Directory -Force $pack | Out-Null
Copy-Item (Join-Path $loaderStage 'SWH2ModLoader') $pack -Recurse
New-Item -ItemType Directory -Force (Join-Path $pack 'Mods') | Out-Null
foreach ($mod in $mods) { Copy-Item (Join-Path $modStages[$mod] "Mods\$mod") (Join-Path $pack 'Mods') -Recurse }
# UTF-8 with BOM so every Notepad shows the Cyrillic text correctly.
$guide = Get-Content -Encoding UTF8 -Raw (Join-Path $root 'docs\GUIDE_RU.md')
[IO.File]::WriteAllText((Join-Path $pack 'README.txt'), $guide, (New-Object Text.UTF8Encoding $true))

Write-Host "Archives:"
New-Zip (Join-Path $dist "$packName.zip") $pack
New-Zip (Join-Path $dist "nexus\SWH2ModLoader-$loaderVersion.zip") $loaderStage
foreach ($a in $modArchives) { New-Zip (Join-Path $dist "nexus\$($a[0])") $a[1] }

# ---- optional: copy into the game folder ---------------------------------------------------
if ($Deploy) {
    if (-not (Test-Path (Join-Path $Game 'SteamWorld Heist 2.exe'))) { throw "Game not found in '$Game' (use -Game <folder>)" }
    Write-Host "Deploying to $Game..."
    # Loader: overwrite program files, keep Backup\, profile.json, Reports\ etc.
    Copy-Item (Join-Path $pack 'SWH2ModLoader') $Game -Recurse -Force
    foreach ($mod in $mods) {
        $target = Join-Path $Game "Mods\$mod"
        if (Test-Path $target) { Remove-Item -Recurse -Force $target }   # mod settings live in profile.json, not here
        New-Item -ItemType Directory -Force (Join-Path $Game 'Mods') | Out-Null
        Copy-Item (Join-Path $pack "Mods\$mod") (Join-Path $Game 'Mods') -Recurse
    }
    Write-Host "Deployed. Run $Game\SWH2ModLoader\SWH2ModLoader.exe, tick mods, click 'Apply to game'."
}

Write-Host ""
Write-Host "Done: $dist"
Write-Host "  $packName\   <- copy its contents into the game folder (guide: README.txt)"
