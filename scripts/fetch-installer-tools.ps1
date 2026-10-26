# Downloads the Inno Setup compiler (the Tools.InnoSetup NuGet package, which unpacks anywhere and needs no
# installation) into third_party/inno. Not committed. Used by scripts/publish.ps1 to build the installer.
$ErrorActionPreference = 'Stop'
$version = '6.4.3'
$dest = Join-Path $PSScriptRoot '..\third_party\inno'
if (Test-Path (Join-Path $dest 'ISCC.exe')) { Write-Host 'Inno Setup already present'; return }
New-Item -ItemType Directory -Force $dest | Out-Null
$zip = Join-Path ([IO.Path]::GetTempPath()) "tools.innosetup.$version.zip"
Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/tools.innosetup/$version/tools.innosetup.$version.nupkg" -OutFile $zip
$tmp = Join-Path ([IO.Path]::GetTempPath()) "inno-$version"
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive $zip $tmp
Copy-Item (Join-Path $tmp 'tools\*') $dest -Recurse -Force
Remove-Item $zip, $tmp -Recurse -Force
Write-Host "Inno Setup $version ready in $dest"
