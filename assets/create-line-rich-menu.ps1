#Requires -Version 7
[CmdletBinding()]
param(
    [string]$MascotPath = (Join-Path $PSScriptRoot 'line-mimamori-mascot.png'),
    [string]$OutputPath = (Join-Path $PSScriptRoot 'line-rich-menu.png')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$width = 2500
$height = 1686
$columns = @(834, 833, 833)
$rows = @(843, 843)
$columnX = @(0, 834, 1667)
$rowY = @(0, 843)

function ConvertTo-Color {
    param([string]$Hex)
    [System.Drawing.ColorTranslator]::FromHtml($Hex)
}

function New-RoundedPath {
    param(
        [System.Drawing.RectangleF]$Rectangle,
        [float]$Radius
    )

    $diameter = $Radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($Rectangle.X, $Rectangle.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Rectangle.X, $Rectangle.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-CenteredText {
    param(
        [System.Drawing.Graphics]$Graphics,
        [string]$Text,
        [System.Drawing.Font]$Font,
        [System.Drawing.Brush]$Brush,
        [System.Drawing.RectangleF]$Rectangle
    )

    $format = [System.Drawing.StringFormat]::new()
    try {
        $format.Alignment = [System.Drawing.StringAlignment]::Center
        $format.LineAlignment = [System.Drawing.StringAlignment]::Center
        $Graphics.DrawString($Text, $Font, $Brush, $Rectangle, $format)
    }
    finally {
        $format.Dispose()
    }
}

function Draw-CheckIcon {
    param($Graphics, [float]$CenterX, [float]$CenterY, $Pen)
    $points = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($CenterX - 78, $CenterY + 4),
        [System.Drawing.PointF]::new($CenterX - 20, $CenterY + 62),
        [System.Drawing.PointF]::new($CenterX + 94, $CenterY - 72)
    )
    $Graphics.DrawLines($Pen, $points)
}

function Draw-ExclamationIcon {
    param($Graphics, [float]$CenterX, [float]$CenterY, $Brush)
    $Graphics.FillRectangle($Brush, $CenterX - 18, $CenterY - 92, 36, 124)
    $Graphics.FillEllipse($Brush, $CenterX - 21, $CenterY + 57, 42, 42)
}

function Draw-CrossIcon {
    param($Graphics, [float]$CenterX, [float]$CenterY, $Brush)
    $Graphics.FillRectangle($Brush, $CenterX - 25, $CenterY - 94, 50, 188)
    $Graphics.FillRectangle($Brush, $CenterX - 94, $CenterY - 25, 188, 50)
}

function Draw-ClockIcon {
    param($Graphics, [float]$CenterX, [float]$CenterY, $Pen)
    $Graphics.DrawEllipse($Pen, $CenterX - 92, $CenterY - 92, 184, 184)
    $Graphics.DrawLine($Pen, $CenterX, $CenterY, $CenterX, $CenterY - 57)
    $Graphics.DrawLine($Pen, $CenterX, $CenterY, $CenterX + 55, $CenterY + 31)
}

function Draw-FamilyIcon {
    param($Graphics, [float]$CenterX, [float]$CenterY, $Brush)
    $Graphics.FillEllipse($Brush, $CenterX - 105, $CenterY - 105, 86, 86)
    $Graphics.FillEllipse($Brush, $CenterX + 19, $CenterY - 105, 86, 86)
    $Graphics.FillEllipse($Brush, $CenterX - 60, $CenterY - 42, 120, 120)
    $Graphics.FillEllipse($Brush, $CenterX - 135, $CenterY + 4, 126, 92)
    $Graphics.FillEllipse($Brush, $CenterX + 9, $CenterY + 4, 126, 92)
}

function Draw-MessageIcon {
    param($Graphics, [float]$CenterX, [float]$CenterY, $Brush)
    $bubble = [System.Drawing.RectangleF]::new($CenterX - 120, $CenterY - 90, 240, 164)
    $path = New-RoundedPath -Rectangle $bubble -Radius 42
    try {
        $Graphics.FillPath($Brush, $path)
        $tail = [System.Drawing.PointF[]]@(
            [System.Drawing.PointF]::new($CenterX - 63, $CenterY + 63),
            [System.Drawing.PointF]::new($CenterX - 84, $CenterY + 122),
            [System.Drawing.PointF]::new($CenterX - 10, $CenterY + 69)
        )
        $Graphics.FillPolygon($Brush, $tail)
    }
    finally {
        $path.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $MascotPath -PathType Leaf)) {
    throw "Mascot image not found: $MascotPath"
}

$bitmap = [System.Drawing.Bitmap]::new($width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$mascot = [System.Drawing.Image]::FromFile($MascotPath)

try {
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    $background = [System.Drawing.SolidBrush]::new((ConvertTo-Color '#F5EFE4'))
    $graphics.FillRectangle($background, 0, 0, $width, $height)
    $background.Dispose()

    $fontFamily = 'Yu Gothic UI'
    $titleFont = [System.Drawing.Font]::new($fontFamily, 90, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $subtitleFont = [System.Drawing.Font]::new($fontFamily, 41, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $tinyFont = [System.Drawing.Font]::new($fontFamily, 32, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)

    $buttons = @(
        @{ Row = 0; Col = 0; Title = '助けて';       Subtitle = '緊急連絡';       Accent = '#C9414B'; Surface = '#FFF2F1'; Icon = 'alert' }
        @{ Row = 0; Col = 1; Title = '体調が悪い';   Subtitle = '家族へお知らせ'; Accent = '#D9772A'; Surface = '#FFF5E8'; Icon = 'cross' }
        @{ Row = 0; Col = 2; Title = '大丈夫';       Subtitle = '安心を伝える';   Accent = '#2E7D5B'; Surface = '#EDF8F1'; Icon = 'check' }
        @{ Row = 1; Col = 0; Title = '今日の様子';   Subtitle = '活動を確認';     Accent = '#35738D'; Surface = '#EEF7F8'; Icon = 'mascot' }
        @{ Row = 1; Col = 1; Title = '家族に連絡';   Subtitle = 'すぐに連絡';     Accent = '#8A5A44'; Surface = '#F8F1EA'; Icon = 'family' }
        @{ Row = 1; Col = 2; Title = 'Web版';        Subtitle = '3Dでかんたん操作'; Accent = '#59636E'; Surface = '#F1F3F4'; Icon = 'message' }
    )

    foreach ($button in $buttons) {
        $cellX = $columnX[$button.Col]
        $cellY = $rowY[$button.Row]
        $cellWidth = $columns[$button.Col]
        $cellHeight = $rows[$button.Row]

        $cardRect = [System.Drawing.RectangleF]::new($cellX + 24, $cellY + 24, $cellWidth - 48, $cellHeight - 48)
        $shadowRect = [System.Drawing.RectangleF]::new($cardRect.X + 8, $cardRect.Y + 12, $cardRect.Width, $cardRect.Height)
        $shadowPath = New-RoundedPath -Rectangle $shadowRect -Radius 54
        $cardPath = New-RoundedPath -Rectangle $cardRect -Radius 54

        $shadowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(24, 50, 36, 28))
        $surfaceBrush = [System.Drawing.SolidBrush]::new((ConvertTo-Color $button.Surface))
        $accentBrush = [System.Drawing.SolidBrush]::new((ConvertTo-Color $button.Accent))
        $accentPen = [System.Drawing.Pen]::new((ConvertTo-Color $button.Accent), 26)
        $accentPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $accentPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

        try {
            $graphics.FillPath($shadowBrush, $shadowPath)
            $graphics.FillPath($surfaceBrush, $cardPath)

            # A thick top rail makes action categories easy to distinguish at a glance.
            $railRect = [System.Drawing.RectangleF]::new($cardRect.X, $cardRect.Y, $cardRect.Width, 26)
            $railPath = New-RoundedPath -Rectangle $railRect -Radius 13
            $graphics.FillPath($accentBrush, $railPath)
            $railPath.Dispose()

            $iconX = $cellX + ($cellWidth / 2)
            $iconY = $cellY + 238

            if ($button.Icon -eq 'mascot') {
                # Make the crafted CG the visual hero of the status card, not a tiny icon.
                $graphics.DrawImage($mascot, $iconX - 242, $cellY + 24, 484, 484)
            }
            else {
                $graphics.FillEllipse($accentBrush, $iconX - 132, $iconY - 132, 264, 264)
                $whiteBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
                $whitePen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 27)
                $whitePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
                $whitePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
                try {
                    switch ($button.Icon) {
                        'alert'   { Draw-ExclamationIcon $graphics $iconX $iconY $whiteBrush }
                        'cross'   { Draw-CrossIcon $graphics $iconX $iconY $whiteBrush }
                        'check'   { Draw-CheckIcon $graphics $iconX $iconY $whitePen }
                        'family'  { Draw-FamilyIcon $graphics $iconX $iconY $whiteBrush }
                        'message' { Draw-MessageIcon $graphics $iconX $iconY $whiteBrush }
                    }
                }
                finally {
                    $whiteBrush.Dispose()
                    $whitePen.Dispose()
                }
            }

            $titleBrush = [System.Drawing.SolidBrush]::new((ConvertTo-Color '#2D2926'))
            $subtitleBrush = [System.Drawing.SolidBrush]::new((ConvertTo-Color $button.Accent))
            try {
                $titleY = if ($button.Icon -eq 'mascot') { $cellY + 500 } else { $cellY + 410 }
                $subtitleY = if ($button.Icon -eq 'mascot') { $cellY + 628 } else { $cellY + 568 }
                Draw-CenteredText $graphics $button.Title $titleFont $titleBrush ([System.Drawing.RectangleF]::new($cellX + 42, $titleY, $cellWidth - 84, 138))
                Draw-CenteredText $graphics $button.Subtitle $subtitleFont $subtitleBrush ([System.Drawing.RectangleF]::new($cellX + 42, $subtitleY, $cellWidth - 84, 70))

                $tapBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(225, (ConvertTo-Color $button.Accent)))
                try {
                    $tapY = if ($button.Icon -eq 'mascot') { $cellY + 722 } else { $cellY + 684 }
                    Draw-CenteredText $graphics 'タップして選択' $tinyFont $tapBrush ([System.Drawing.RectangleF]::new($cellX + 42, $tapY, $cellWidth - 84, 52))
                }
                finally {
                    $tapBrush.Dispose()
                }
            }
            finally {
                $titleBrush.Dispose()
                $subtitleBrush.Dispose()
            }
        }
        finally {
            $shadowBrush.Dispose()
            $surfaceBrush.Dispose()
            $accentBrush.Dispose()
            $accentPen.Dispose()
            $shadowPath.Dispose()
            $cardPath.Dispose()
        }
    }

    $outputDirectory = Split-Path -Parent $OutputPath
    if ($outputDirectory -and -not (Test-Path -LiteralPath $outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
    $bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    if ($titleFont) { $titleFont.Dispose() }
    if ($subtitleFont) { $subtitleFont.Dispose() }
    if ($tinyFont) { $tinyFont.Dispose() }
    $mascot.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}

Write-Host "Created LINE rich menu artwork: $OutputPath"
