# Builds the release package into dist/:
#   dist/Muro/                      the install folder (app, engine, screen saver, libmpv, ffmpeg, licences)
#   dist/Muro-<version>-win-x64.zip the same folder zipped (unzip anywhere and run Muro.exe)
#   dist/Muro-Setup-<version>.exe   the per-user installer (no admin rights)
#   dist/SHA256SUMS.txt
# The release asset names matter: the app's update check only offers a release that has an .exe or .msi, or a
# .zip whose name contains "win".
# Usage: ./scripts/publish.ps1 [-SkipInstaller] [-Configuration Release]
param([string]$Configuration = 'Release', [switch]$SkipInstaller)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root

[xml]$props = Get-Content Directory.Build.props
$version = @($props.Project.PropertyGroup.Version)[0]
if (-not $version) { throw 'No <Version> in Directory.Build.props' }
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'Muro'
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null

# One folder, one runtime: the three programs share every file they have in common, so the install is not
# three copies of .NET. Each program keeps its own .exe, .dll, .deps.json and .runtimeconfig.json.
foreach ($project in 'Muro.App', 'Muro.Engine', 'Muro.ScreenSaver') {
    Write-Host "== publishing $project"
    dotnet publish "src/$project" -c $Configuration -r win-x64 --self-contained true -o $stage
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $project" }
}

# Windows only runs a screen saver named *.scr. It is the same program.
Copy-Item (Join-Path $stage 'Muro.ScreenSaver.exe') (Join-Path $stage 'Muro.scr')
Remove-Item (Join-Path $stage 'Muro.ScreenSaver.exe')

# Everything the programs look for next to themselves must be there.
foreach ($needed in 'Muro.exe', 'Muro.Engine.exe', 'Muro.scr', 'libmpv-2.dll', 'ffmpeg\ffmpeg.exe', 'Muro.Engine.dll', 'Muro.ScreenSaver.dll') {
    if (-not (Test-Path (Join-Path $stage $needed))) { throw "missing from the package: $needed (run scripts/fetch-native.ps1?)" }
}

# Licences: Muro's notice, and where the LGPL libraries (mpv, ffmpeg) come from.
$licenses = Join-Path $stage 'licenses'
New-Item -ItemType Directory -Force $licenses | Out-Null
Copy-Item (Join-Path $root 'installer\THIRD-PARTY.txt') $licenses
foreach ($file in '..\NOTICE.md', '..\LICENSE', '..\LICENSE.md') {
    $path = Join-Path $root $file
    if (Test-Path $path) { Copy-Item $path $licenses }
}

$zip = Join-Path $dist "Muro-$version-win-x64.zip"
Write-Host "== zipping $zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

if (-not $SkipInstaller) {
    & (Join-Path $PSScriptRoot 'fetch-installer-tools.ps1')
    $iscc = Join-Path $root 'third_party\inno\ISCC.exe'
    Write-Host '== building the installer'
    & $iscc "/DAppVersion=$version" "/DStageDir=$stage" (Join-Path $root 'installer\Muro.iss')
    if ($LASTEXITCODE -ne 0) { throw 'installer build failed' }
}

Get-ChildItem $dist -File | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name } |
    Set-Content (Join-Path $dist 'SHA256SUMS.txt')
Get-ChildItem $dist -File | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
