# Downloads the LGPL libmpv and ffmpeg builds into third_party/ (not committed: ~190 MB).
# Pinned to one zhongfly/mpv-winbuild release so builds are reproducible.
# shortcut: upstream deletes builds after about 30 days, so this pin expires; mirror the two archives when that hurts.
$ErrorActionPreference = 'Stop'
$tag = '2026-11-25-d32c910be6'
$base = "https://github.com/zhongfly/mpv-winbuild/releases/download/$tag"
$root = Join-Path $PSScriptRoot '..\third_party'
$seven = @('C:\Program Files\7-Zip\7z.exe', '7z') | Where-Object { Get-Command $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
if (-not $seven) { throw '7-Zip is required to unpack the .7z archives' }
$items = @{
  mpv    = 'mpv-dev-lgpl-x86_64-20261125-git-d32c910be6.7z'
  ffmpeg = 'ffmpeg-lgpl-x86_64-git-c81e35cf2.7z'
}
foreach ($name in $items.Keys) {
  $dest = Join-Path $root $name
  New-Item -ItemType Directory -Force $dest | Out-Null
  $tmp = Join-Path ([IO.Path]::GetTempPath()) $items[$name]
  Invoke-WebRequest "$base/$($items[$name])" -OutFile $tmp
  & $seven x -y "-o$dest" $tmp | Out-Null
  Remove-Item $tmp
}
Write-Host 'native dependencies ready'
