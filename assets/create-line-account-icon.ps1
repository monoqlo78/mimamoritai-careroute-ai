#Requires -Version 7
<#
.SYNOPSIS
    Builds the LINE Official Account profile picture from the Mimamo mascot avatar.

.DESCRIPTION
    The LINE Official Account's own name and profile picture can only be changed by a
    human in LINE Official Account Manager (manager.line.biz) -- there is no Messaging
    API endpoint for it. This script does the one half that *can* be automated: it
    produces a file that meets LINE's requirements, so the remaining work is a few
    clicks rather than image editing.

    LINE requires a square image and renders it inside a circular mask. The source
    avatar (wwwroot/images/mimamo-avatar.png) is already a circular RGBA cut-out, and a
    transparent PNG uploaded as a profile picture is composited onto black by LINE --
    which would put a black ring around Mimamo's head. So the avatar is flattened onto
    the app's pale mint brand background first, and the mascot is inset slightly so
    nothing touches the edge of LINE's circular crop.

.PARAMETER SourcePath
    The RGBA mascot avatar to use. Defaults to the app's 512x512 face avatar.

.PARAMETER OutputPath
    Where the flattened square PNG is written. Defaults to assets/line/mimamo-line-account-icon.png.

.PARAMETER Size
    Output edge length in pixels. LINE recommends at least 640x640; the default 1024
    stays well inside the ~3MB upload limit while surviving LINE's own downscaling.

.PARAMETER BackgroundHex
    Solid background colour composited behind the transparent avatar.

.EXAMPLE
    ./assets/create-line-account-icon.ps1
#>
[CmdletBinding()]
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot '../src/MimamoriTai.Web/wwwroot/images/mimamo-avatar.png'),
    [string]$OutputPath = (Join-Path $PSScriptRoot 'line/mimamo-line-account-icon.png'),
    [ValidateRange(640, 4096)]
    [int]$Size = 1024,
    [ValidatePattern('^#[0-9A-Fa-f]{6}$')]
    [string]$BackgroundHex = '#E8F6F4'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
    throw "Mascot avatar not found: $SourcePath"
}

# LINE crops the picture to a circle. Leaving a margin keeps the antenna heart and the
# outline of the head inside that circle instead of being shaved off at the edge.
$inset = [int][Math]::Round($Size * 0.06)
$drawSize = $Size - ($inset * 2)

$bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$source = [System.Drawing.Image]::FromFile($SourcePath)

try {
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    $background = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($BackgroundHex))
    try {
        $graphics.FillRectangle($background, 0, 0, $Size, $Size)
    }
    finally {
        $background.Dispose()
    }

    $graphics.DrawImage($source, $inset, $inset, $drawSize, $drawSize)

    $outputDirectory = Split-Path -Parent $OutputPath
    if ($outputDirectory -and -not (Test-Path -LiteralPath $outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }

    $bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $source.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}

$sizeKb = [int]((Get-Item -LiteralPath $OutputPath).Length / 1KB)
Write-Host "Created LINE account icon: $OutputPath (${Size}x${Size}, ${sizeKb}KB)"
Write-Host "Upload it manually in LINE Official Account Manager -- see docs/line-one-touch-setup.md."
