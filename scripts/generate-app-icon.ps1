$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$assetDirectory = Join-Path $root 'Assets'
$output = Join-Path $assetDirectory 'ZDesk.ico'
New-Item -ItemType Directory -Force -Path $assetDirectory | Out-Null

$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$images = @()
foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $inset = [Math]::Max(1, $size * 0.04)
    $circle = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 15, 197, 232))
    $graphics.FillEllipse($circle, $inset, $inset, $size - 2 * $inset, $size - 2 * $inset)
    $font = New-Object System.Drawing.Font 'Segoe UI', ($size * 0.48), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $textSize = $graphics.MeasureString('Z', $font)
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $graphics.DrawString('Z', $font, $white, (($size - $textSize.Width) / 2), (($size - $textSize.Height) / 2 - $size * 0.015))
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256) { $bitmap.Save((Join-Path $assetDirectory 'ZDesk.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $images += ,$stream.ToArray()
    $stream.Dispose(); $white.Dispose(); $font.Dispose(); $circle.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}

$file = [System.IO.File]::Create($output)
$writer = New-Object System.IO.BinaryWriter $file
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
for ($index = 0; $index -lt $images.Count; $index++) {
    $size = $sizes[$index]
    $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
    $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$index].Length); $writer.Write([uint32]$offset)
    $offset += $images[$index].Length
}
foreach ($image in $images) { $writer.Write($image) }
$writer.Dispose(); $file.Dispose()
Write-Host "Generated $output"
