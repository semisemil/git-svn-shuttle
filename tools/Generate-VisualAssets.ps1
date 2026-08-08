[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\GitSvnShuttle.Vsix\Assets')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null

function New-RoundPen {
    param(
        [System.Windows.Media.Brush]$Brush,
        [double]$Thickness
    )

    $pen = [System.Windows.Media.Pen]::new($Brush, $Thickness)
    $pen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
    $pen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
    $pen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
    return $pen
}

function Write-ShuttleIcon {
    param(
        [int]$Size,
        [string]$FileName
    )

    $visual = [System.Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    try {
        $scale = $Size / 200.0
        $context.PushTransform([System.Windows.Media.ScaleTransform]::new($scale, $scale))

        $gradient = [System.Windows.Media.LinearGradientBrush]::new()
        $gradient.StartPoint = [System.Windows.Point]::new(0.1, 0.05)
        $gradient.EndPoint = [System.Windows.Point]::new(0.9, 0.95)
        $gradient.GradientStops.Add([System.Windows.Media.GradientStop]::new(
            [System.Windows.Media.Color]::FromRgb(75, 130, 255), 0.0))
        $gradient.GradientStops.Add([System.Windows.Media.GradientStop]::new(
            [System.Windows.Media.Color]::FromRgb(37, 84, 206), 1.0))

        $context.DrawRoundedRectangle(
            $gradient,
            $null,
            [System.Windows.Rect]::new(8, 8, 184, 184),
            42,
            42)

        $highlight = [System.Windows.Media.SolidColorBrush]::new(
            [System.Windows.Media.Color]::FromArgb(75, 255, 255, 255))
        $context.DrawRoundedRectangle(
            $null,
            (New-RoundPen $highlight 3),
            [System.Windows.Rect]::new(11, 11, 178, 178),
            39,
            39)

        $white = [System.Windows.Media.Brushes]::White
        $softWhite = [System.Windows.Media.SolidColorBrush]::new(
            [System.Windows.Media.Color]::FromArgb(180, 255, 255, 255))
        $arrowPen = New-RoundPen $white 12
        $guidePen = New-RoundPen $softWhite 5

        $context.DrawLine($arrowPen, [System.Windows.Point]::new(68, 139), [System.Windows.Point]::new(68, 61))
        $context.DrawLine($arrowPen, [System.Windows.Point]::new(68, 61), [System.Windows.Point]::new(43, 86))
        $context.DrawLine($arrowPen, [System.Windows.Point]::new(68, 61), [System.Windows.Point]::new(93, 86))

        $context.DrawLine($arrowPen, [System.Windows.Point]::new(132, 61), [System.Windows.Point]::new(132, 139))
        $context.DrawLine($arrowPen, [System.Windows.Point]::new(132, 139), [System.Windows.Point]::new(107, 114))
        $context.DrawLine($arrowPen, [System.Windows.Point]::new(132, 139), [System.Windows.Point]::new(157, 114))

        $context.DrawLine($guidePen, [System.Windows.Point]::new(98, 72), [System.Windows.Point]::new(102, 128))
        $context.DrawEllipse($white, $null, [System.Windows.Point]::new(100, 100), 8, 8)

        $context.Pop()
    }
    finally {
        $context.Close()
    }

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $Size,
        $Size,
        96,
        96,
        [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)

    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $outputPath = Join-Path $resolvedOutput $FileName
    $stream = [System.IO.File]::Open($outputPath, [System.IO.FileMode]::Create)
    try {
        $encoder.Save($stream)
    }
    finally {
        $stream.Dispose()
    }

    Write-Output $outputPath
}

Write-ShuttleIcon -Size 90 -FileName 'GitSvnShuttle.Icon.png'
Write-ShuttleIcon -Size 200 -FileName 'GitSvnShuttle.Preview.png'
