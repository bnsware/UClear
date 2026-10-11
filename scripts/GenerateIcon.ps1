$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/UClear/Assets'))
[System.IO.Directory]::CreateDirectory($assetDirectory) | Out-Null
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $inset = [single]($size * 0.04)
    $extent = [single]($size - 2 * $inset)
    $diameter = [single]($size * 0.42)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($inset, $inset, $diameter, $diameter, 180, 90)
    $path.AddArc($inset + $extent - $diameter, $inset, $diameter, $diameter, 270, 90)
    $path.AddArc($inset + $extent - $diameter, $inset + $extent - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($inset, $inset + $extent - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    $background = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#61D9B6'))
    $foreground = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#10281F'))
    $graphics.FillPath($background, $path)
    # Draw the U as vector geometry so the mark is identical at every size.
    $letter = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $letter.AddLine([single]($size*.27), [single]($size*.25), [single]($size*.27), [single]($size*.55))
    $letter.AddBezier([single]($size*.27), [single]($size*.55), [single]($size*.27), [single]($size*.84), [single]($size*.73), [single]($size*.84), [single]($size*.73), [single]($size*.55))
    $letter.AddLine([single]($size*.73), [single]($size*.55), [single]($size*.73), [single]($size*.25))
    $letter.AddLine([single]($size*.73), [single]($size*.25), [single]($size*.59), [single]($size*.25))
    $letter.AddLine([single]($size*.59), [single]($size*.25), [single]($size*.59), [single]($size*.55))
    $letter.AddBezier([single]($size*.59), [single]($size*.55), [single]($size*.59), [single]($size*.67), [single]($size*.41), [single]($size*.67), [single]($size*.41), [single]($size*.55))
    $letter.AddLine([single]($size*.41), [single]($size*.55), [single]($size*.41), [single]($size*.25))
    $letter.CloseFigure()
    $graphics.FillPath($foreground, $letter)
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += ,$stream.ToArray()
    if ($size -eq 256) { $bitmap.Save((Join-Path $assetDirectory 'UClear.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $stream.Dispose(); $letter.Dispose(); $path.Dispose(); $foreground.Dispose(); $background.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$file = [System.IO.File]::Create((Join-Path $assetDirectory 'UClear.ico'))
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$index].Length); $writer.Write([uint32]$offset)
        $offset += $images[$index].Length
    }
    foreach ($data in $images) { $writer.Write([byte[]]$data) }
} finally { $writer.Dispose(); $file.Dispose() }
