param(
    [Parameter(Mandatory = $true)]
    [string]$InputPng,

    [Parameter(Mandatory = $true)]
    [string]$OutputIco
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore

$inputPath = (Resolve-Path -LiteralPath $InputPng).Path
$outputPath = [System.IO.Path]::GetFullPath($OutputIco)
$sizes = @(16, 24, 32, 48, 64, 128, 256)

$source = New-Object System.Windows.Media.Imaging.BitmapImage
$source.BeginInit()
$source.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
$source.UriSource = New-Object System.Uri($inputPath)
$source.EndInit()
$source.Freeze()

$images = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($size in $sizes) {
    $scaleX = $size / $source.PixelWidth
    $scaleY = $size / $source.PixelHeight
    $transform = New-Object System.Windows.Media.ScaleTransform($scaleX, $scaleY)
    $resized = New-Object System.Windows.Media.Imaging.TransformedBitmap($source, $transform)

    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($resized))
    $pngStream = New-Object System.IO.MemoryStream
    $encoder.Save($pngStream)
    $images.Add($pngStream.ToArray())
    $pngStream.Dispose()
}

$iconStream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($iconStream)
$writer.Write([uint16]0)          # reserved
$writer.Write([uint16]1)          # icon
$writer.Write([uint16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)        # palette colors
    $writer.Write([byte]0)        # reserved
    $writer.Write([uint16]1)      # color planes
    $writer.Write([uint16]32)     # bits per pixel
    $writer.Write([uint32]$images[$i].Length)
    $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}

foreach ($image in $images) {
    $writer.Write($image)
}

$writer.Flush()
[System.IO.File]::WriteAllBytes($outputPath, $iconStream.ToArray())
$writer.Dispose()
$iconStream.Dispose()

Write-Host "Created $outputPath with sizes: $($sizes -join ', ')"
