# MemoPad 아이콘 만들기: 무광 검정 판 + 샴페인 골드 메모지 (오른쪽 아래 접힌 귀퉁이, 가는 글 줄) → MemoPad.ico (16~256), MemoPad.png (256)
#   powershell -NoProfile -ExecutionPolicy Bypass -File Assets\Build-Icon.ps1 [-Sheet <png>]   (-Sheet: 크기별 미리 보기 한 장)
param([string]$Sheet = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$convert = [System.Windows.Media.BrushConverter]::new()
function Brush([string]$color) { return $convert.ConvertFromString($color) }
function Gradient([string[]]$colors, [double]$x1, [double]$y1, [double]$x2, [double]$y2) {
    $brush = [System.Windows.Media.LinearGradientBrush]::new()
    $brush.StartPoint = [System.Windows.Point]::new($x1, $y1); $brush.EndPoint = [System.Windows.Point]::new($x2, $y2)
    for ($i = 0; $i -lt $colors.Count; $i++) {
        $brush.GradientStops.Add([System.Windows.Media.GradientStop]::new([System.Windows.Media.ColorConverter]::ConvertFromString($colors[$i]), $i / [Math]::Max(1, $colors.Count - 1)))
    }
    return $brush
}

function Render([int]$size) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.PushTransform([System.Windows.Media.ScaleTransform]::new($size / 64.0, $size / 64.0))
    $small = $size -le 32

    # 판: 무광 검정 + 은은한 가운데 빛
    $tile = [System.Windows.Rect]::new(2, 2, 60, 60)
    $dc.DrawRoundedRectangle((Gradient @('#FF1E2027', '#FF0E0F13') 0 0 1 1), $null, $tile, 14, 14)
    if (-not $small) {
        $glow = [System.Windows.Media.RadialGradientBrush]::new()
        $glow.GradientOrigin = [System.Windows.Point]::new(0.35, 0.3); $glow.Center = $glow.GradientOrigin
        $glow.RadiusX = 0.75; $glow.RadiusY = 0.75
        $glow.GradientStops.Add([System.Windows.Media.GradientStop]::new([System.Windows.Media.Color]::FromArgb(0x30, 0xE9, 0xD1, 0x93), 0))
        $glow.GradientStops.Add([System.Windows.Media.GradientStop]::new([System.Windows.Media.Color]::FromArgb(0x00, 0xE9, 0xD1, 0x93), 1))
        $dc.DrawRoundedRectangle($glow, $null, $tile, 14, 14)
        $edge = [System.Windows.Media.Pen]::new((Brush '#33E9D193'), 0.6)
        $dc.DrawRoundedRectangle($null, $edge, [System.Windows.Rect]::new(2.3, 2.3, 59.4, 59.4), 13.7, 13.7)
    }

    # 메모지: 골드 박 (접힌 귀퉁이 빼고)
    $l = 15; $t = 14; $r = 49; $b = 50; $fold = if ($small) { 10 } else { 11 }
    $sheet = [System.Windows.Media.Geometry]::Parse("M $l,$($t + 3) Q $l,$t $($l + 3),$t L $($r - 3),$t Q $r,$t $r,$($t + 3) L $r,$($b - $fold) L $($r - $fold),$b L $($l + 3),$b Q $l,$b $l,$($b - 3) Z")
    $gold = Gradient @('#FF9A7832', '#FFE9D193', '#FFC9A452', '#FFFFF2C4', '#FFD2AE5E', '#FF8F6E2C') 0 0 1 1
    $dc.DrawGeometry($gold, $null, $sheet)
    # 접힌 귀퉁이: 조금 어두운 금
    $corner = [System.Windows.Media.Geometry]::Parse("M $r,$($b - $fold) L $($r - $fold + 1.5),$($b - $fold) Q $($r - $fold),$($b - $fold) $($r - $fold),$($b - $fold + 1.5) L $($r - $fold),$b Z")
    $dc.DrawGeometry((Gradient @('#FFFFF2C4', '#FFB38F45') 0 0 1 1), $null, $corner)

    # 글 줄
    $ink = [System.Windows.Media.Pen]::new((Brush '#CC2A2116'), $(if ($small) { 3.2 } else { 2.4 }))
    $ink.StartLineCap = 'Round'; $ink.EndLineCap = 'Round'
    if ($small) {
        $dc.DrawLine($ink, [System.Windows.Point]::new(21, 24), [System.Windows.Point]::new(43, 24))
        $dc.DrawLine($ink, [System.Windows.Point]::new(21, 33), [System.Windows.Point]::new(36, 33))
    } else {
        $dc.DrawLine($ink, [System.Windows.Point]::new(21, 23), [System.Windows.Point]::new(43, 23))
        $dc.DrawLine($ink, [System.Windows.Point]::new(21, 30), [System.Windows.Point]::new(43, 30))
        $dc.DrawLine($ink, [System.Windows.Point]::new(21, 37), [System.Windows.Point]::new(33, 37))
    }
    $dc.Pop()
    $dc.Close()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    return $bitmap
}

function Png($bitmap) {
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    $encoder.Save($stream)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    return , $bytes
}

$frames = @()
foreach ($size in $sizes) {
    $bytes = Png (Render $size)
    $frames += , $bytes
    if ($size -eq 256) { [System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'MemoPad.png'), $bytes) }
}

# ICO: 머리 + 크기마다 PNG 한 장
$output = [System.IO.File]::Create((Join-Path $PSScriptRoot 'MemoPad.ico'))
$writer = [System.IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]
        $writer.Write([byte]$(if ($s -ge 256) { 0 } else { $s })); $writer.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
} finally { $writer.Dispose(); $output.Dispose() }

if ($Sheet) {
    # 미리 보기: 크기별로 어두운 바탕 · 밝은 바탕에 (작은 것은 4배 확대해 화소 확인)
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.DrawRectangle((Brush '#FF1C1C1E'), $null, [System.Windows.Rect]::new(0, 0, 900, 200))
    $dc.DrawRectangle((Brush '#FFF3F3F3'), $null, [System.Windows.Rect]::new(0, 200, 900, 200))
    $x = 10
    foreach ($size in @(256, 48, 32, 24, 16)) {
        $bitmap = Render $size
        $shown = if ($size -le 48) { $size * 3 } else { 180 }
        foreach ($y in @(10, 210)) { $dc.DrawImage($bitmap, [System.Windows.Rect]::new($x, $y, $shown, $shown)) }
        $x += $shown + 20
    }
    $dc.Close()
    $sheetBitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(900, 400, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $sheetBitmap.Render($visual)
    [System.IO.File]::WriteAllBytes($Sheet, (Png $sheetBitmap))
}
Write-Host "MemoPad.ico ($($sizes -join ', '))"
