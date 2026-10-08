# Generate Assets/app.ico (single-frame 256px PNG-in-ICO)
# Usage: powershell -ExecutionPolicy Bypass -File scripts/make-icon.ps1
# NOTE: keep this file ASCII-only; Windows PowerShell 5.1 misreads BOM-less UTF-8 as ANSI/GBK.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot "..\src\LesTool\Assets\app.ico"
$dir = Split-Path $out -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

$size = 256
$bmp = New-Object System.Drawing.Bitmap($size, $size)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

# dark disc
$bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 32, 32, 40))
$g.FillEllipse($bg, 8, 8, 240, 240)

# four accent arcs (radial sectors)
$accentColor = [System.Drawing.Color]::FromArgb(255, 0, 153, 255)
foreach ($start in @(270, 0, 90, 180)) {
    $accent = [System.Drawing.Pen]::new($accentColor, [float]22)
    try { $g.DrawArc($accent, 36, 36, 184, 184, [float]($start + 10), [float]70) } finally { $accent.Dispose() }
}

# center dot
$dot = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
$g.FillEllipse($dot, 118, 118, 20, 20)
$g.Dispose()

# wrap PNG payload into ICO container (Vista+ supports PNG-compressed icons)
$ms = New-Object System.IO.MemoryStream
$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $ms.ToArray()

$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ico)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]1)  # reserved / type=icon / count=1
$bw.Write([byte]0); $bw.Write([byte]0)                            # 256px encoded as 0
$bw.Write([byte]0); $bw.Write([byte]0)                            # colors / reserved
$bw.Write([uint16]1); $bw.Write([uint16]32)                       # planes / bpp
$bw.Write([uint32]$png.Length); $bw.Write([uint32]22)             # data size / offset
$bw.Write($png)
$bw.Flush()
[System.IO.File]::WriteAllBytes($out, $ico.ToArray())
Write-Host "OK: $out ($($ico.Length) bytes)"
