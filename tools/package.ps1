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

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\package.ps1
  powershell -ExecutionPolicy Bypass -File tools\package.ps1 -Deploy
  powershell -ExecutionPolicy Bypass -File tools\package.ps1 -Deploy -Game "D:\Games\SteamWorld Heist 2"
#>
param(
    [switch]$Deploy,
    [string]$Game = 'C:\Program Files (x86)\Steam\steamapps\common\SteamWorld Heist 2'
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'stage'
$mods = 'Randomizer', 'Turbo', 'Challenges', 'CheatMenu'
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Force $stage, (Join-Path $dist 'nexus') | Out-Null

function Invoke-Dotnet([string[]]$dotnetArgs) {
    & dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($dotnetArgs -join ' ') failed" }
}

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
$loaderStage = Join-Path $stage 'loader'
$loaderDir = Join-Path $loaderStage 'SWH2ModLoader'

$publish = @('-c', 'Release', '-o', $loaderDir, '-p:DebugType=none', '-p:GenerateDocumentationFile=false')
Write-Host "Publishing loader $loaderVersion..."
Invoke-Dotnet (@('publish', (Join-Path $root 'SWH2.ModLoader\SWH2.ModLoader.csproj')) + $publish)
Invoke-Dotnet (@('publish', (Join-Path $root 'SWH2.Cli\SWH2.Cli.csproj')) + $publish)
Copy-Item (Join-Path $root 'docs\NEXUS_LOADER.md') (Join-Path $loaderDir 'README.md')
Copy-Item (Join-Path $root 'docs\MAKING_MODS.md') (Join-Path $loaderDir 'MAKING_MODS.md')
New-Item -ItemType Directory -Force (Join-Path $loaderStage 'Mods') | Out-Null
Set-Content -Encoding UTF8 (Join-Path $loaderStage 'Mods\PUT_MODS_HERE.txt') 'Each mod goes in its own folder here: Mods\<ModId>\mod.json'

# ---- mods ---------------------------------------------------------------------------------
$modArchives = @()
foreach ($mod in $mods) {
    Write-Host "Building $mod..."
    Invoke-Dotnet @('build', (Join-Path $root "Mods\$mod\$mod.csproj"), '-c', 'Release')
    $out = Join-Path $root "Mods\$mod\bin\Release\$mod"
    $manifest = Get-Content (Join-Path $out 'mod.json') -Raw | ConvertFrom-Json
    $modStage = Join-Path $stage $mod
    $modDir = Join-Path $modStage "Mods\$mod"
    New-Item -ItemType Directory -Force $modDir | Out-Null
    Copy-Item (Join-Path $out '*') $modDir -Recurse
    if (Test-Path (Join-Path $modDir 'SWH2.Core.dll')) { throw "$mod output contains SWH2.Core.dll; the loader must provide it" }
    $modArchives += , @("$mod-$($manifest.version).zip", $modStage)
}

# ---- all-in-one pack: loader + every mod + guide, laid out like the game folder ------------
$packName = "SWH2-ModPack-$loaderVersion"
$pack = Join-Path $dist $packName
New-Item -ItemType Directory -Force $pack | Out-Null
Copy-Item (Join-Path $loaderStage 'SWH2ModLoader') $pack -Recurse
New-Item -ItemType Directory -Force (Join-Path $pack 'Mods') | Out-Null
foreach ($mod in $mods) { Copy-Item (Join-Path $stage "$mod\Mods\$mod") (Join-Path $pack 'Mods') -Recurse }
# UTF-8 with BOM so every Notepad shows the Cyrillic text correctly.
$guide = Get-Content -Encoding UTF8 -Raw (Join-Path $root 'docs\GUIDE_RU.md')
[IO.File]::WriteAllText((Join-Path $pack 'README.txt'), $guide, (New-Object Text.UTF8Encoding $true))

Write-Host "Archives:"
New-Zip (Join-Path $dist "$packName.zip") $pack
New-Zip (Join-Path $dist "nexus\SWH2ModLoader-$loaderVersion.zip") $loaderStage
foreach ($a in $modArchives) { New-Zip (Join-Path $dist "nexus\$($a[0])") $a[1] }
Remove-Item -Recurse -Force $stage

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
