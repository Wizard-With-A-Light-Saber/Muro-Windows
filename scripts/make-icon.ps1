# Builds assets/muro.ico from the macOS app icon (Muro/Icon/AppIcon.icns): takes the largest PNG
# inside the .icns and writes PNG-compressed icon images at 16 to 256 px.
# Usage: pwsh scripts/make-icon.ps1 [-Icns <path>] [-Out <path>]
param(
    [string]$Icns = (Join-Path $PSScriptRoot '..\..\Muro\Icon\AppIcon.icns'),
    [string]$Out = (Join-Path $PSScriptRoot '..\assets\muro.ico')
)
Add-Type -AssemblyName System.Drawing

# [int] casts matter: -shl on a [byte] stays a byte and silently truncates.
function ReadBE([byte[]]$b, [int]$at) { ([int]$b[$at] -shl 24) -bor ([int]$b[$at + 1] -shl 16) -bor ([int]$b[$at + 2] -shl 8) -bor [int]$b[$at + 3] }

$bytes = [IO.File]::ReadAllBytes($Icns)
$best = $null
$pos = 8
while ($pos + 8 -le $bytes.Length) {
    $len = ReadBE $bytes ($pos + 4)
    if ($len -lt 8) { break }
    $isPng = $len -gt 16 -and $bytes[$pos + 8] -eq 0x89 -and $bytes[$pos + 9] -eq 0x50
    if ($isPng -and ($null -eq $best -or $len -gt $best.Length)) {
        $best = New-Object byte[] ($len - 8)
        [Array]::Copy($bytes, $pos + 8, $best, 0, $len - 8)
    }
    $pos += $len
}
if ($null -eq $best) { throw "no PNG image found in $Icns" }

$source = [Drawing.Image]::FromStream((New-Object IO.MemoryStream (, $best)))
$images = foreach ($size in 16, 24, 32, 48, 64, 128, 256) {
    $bmp = New-Object Drawing.Bitmap $size, $size
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality'; $g.PixelOffsetMode = 'HighQuality'
    $g.DrawImage($source, 0, 0, $size, $size); $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Data = $ms.ToArray() }
}

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$buffer = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $buffer
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]@($images).Count)
$offset = 6 + 16 * @($images).Count
foreach ($i in $images) {
    $dim = if ($i.Size -ge 256) { 0 } else { $i.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$i.Data.Length); $w.Write([uint32]$offset)
    $offset += $i.Data.Length
}
foreach ($i in $images) { $w.Write([byte[]]$i.Data, 0, $i.Data.Length) }
$w.Flush()
[IO.File]::WriteAllBytes($Out, $buffer.ToArray())
"wrote $Out ($((Get-Item $Out).Length) bytes)"
