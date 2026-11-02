# Generates the TBD Movies application icon (multi-size .ico) from code, so the icon is reproducible
# and versioned as source rather than a checked-in binary of unknown origin.
#
# Design: Unified Fluent Blue & Obsidian Mica rounded tile base (#18222D / #111822) with a refined
# Fluent Blue accent frame (#0078D4) and central Play Prism in Fluent Blue (#0078D4 / #0067C0 / #60CDFF).
#
# Output: a single .ico containing 16, 24, 32, 48, 64, 128 and 256 px frames (PNG-compressed, which
# Windows 10/11 - the app's target - render for the exe, shortcuts and Add/Remove Programs).

param(
    [string]$OutPath = "$PSScriptRoot/../src/TBDMovies.UI/Assets/TBD-Movies.ico"
)

Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256

function New-Frame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded-square tile with deep obsidian mica gradient (#18222D to #111822)
    $pad = [Math]::Max(1.0, [float]($s * 0.06))
    $rect = New-Object System.Drawing.RectangleF($pad, $pad, ($s - 2*$pad), ($s - 2*$pad))
    $radius = $rect.Width * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $obsidianTop = [System.Drawing.Color]::FromArgb(255, 24, 34, 45)  # #18222D
    $obsidianBot = [System.Drawing.Color]::FromArgb(255, 17, 24, 34)  # #111822
    $baseBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $obsidianTop, $obsidianBot, 55.0)
    $g.FillPath($baseBrush, $path)

    # Refined Fluent Blue accent outline/frame (#0078D4)
    $frameWidth = [Math]::Max(1.0, [float]($s * 0.035))
    $blueColor = [System.Drawing.Color]::FromArgb(255, 0, 120, 212)    # #0078D4
    $framePen = New-Object System.Drawing.Pen($blueColor, $frameWidth)
    $framePen.Alignment = [System.Drawing.Drawing2D.PenAlignment]::Inset
    $g.DrawPath($framePen, $path)

    # Central Play Prism in Fluent Blue (#0078D4 / #0067C0 / #60CDFF)
    # Optical horizontal centering for right-pointing triangle:
    $prismH = $rect.Height * 0.44
    $prismW = $prismH * 0.866
    $prismLeft = $rect.X + ($rect.Width - $prismW) * 0.5 + ($rect.Width * 0.03)
    $prismRight = $prismLeft + $prismW
    $prismTop = $rect.Y + ($rect.Height - $prismH) * 0.5
    $prismBottom = $prismTop + $prismH
    $prismMidY = $rect.Y + $rect.Height * 0.5

    $pTop = New-Object System.Drawing.PointF($prismLeft, $prismTop)
    $pRight = New-Object System.Drawing.PointF($prismRight, $prismMidY)
    $pBottom = New-Object System.Drawing.PointF($prismLeft, $prismBottom)

    $blueLight = [System.Drawing.Color]::FromArgb(255, 96, 205, 255)  # #60CDFF
    $blueMid   = [System.Drawing.Color]::FromArgb(255, 0, 120, 212)   # #0078D4
    $blueDark  = [System.Drawing.Color]::FromArgb(255, 0, 103, 192)   # #0067C0

    if ($s -le 24) {
        # At very small sizes, render a unified Fluent Blue gradient triangle for maximum clarity
        $triPath = New-Object System.Drawing.Drawing2D.GraphicsPath
        $triPath.AddLine($pTop, $pRight)
        $triPath.AddLine($pRight, $pBottom)
        $triPath.CloseFigure()
        $triRect = New-Object System.Drawing.RectangleF($prismLeft, $prismTop, $prismW, $prismH)
        $triBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($triRect, $blueLight, $blueDark, 45.0)
        $g.FillPath($triBrush, $triPath)
        $triBrush.Dispose()
        $triPath.Dispose()
    } else {
        # Faceted Play Prism with jewel-like cinema lighting
        $pCenter = New-Object System.Drawing.PointF(($prismLeft + $prismW * 0.36), $prismMidY)

        # Top facet (Light reflection)
        $fTop = New-Object System.Drawing.Drawing2D.GraphicsPath
        $fTop.AddLine($pTop, $pRight)
        $fTop.AddLine($pRight, $pCenter)
        $fTop.CloseFigure()
        $bTop = New-Object System.Drawing.SolidBrush($blueLight)
        $g.FillPath($bTop, $fTop)
        $bTop.Dispose(); $fTop.Dispose()

        # Bottom facet (Deep reflection)
        $fBot = New-Object System.Drawing.Drawing2D.GraphicsPath
        $fBot.AddLine($pBottom, $pRight)
        $fBot.AddLine($pRight, $pCenter)
        $fBot.CloseFigure()
        $bBot = New-Object System.Drawing.SolidBrush($blueDark)
        $g.FillPath($bBot, $fBot)
        $bBot.Dispose(); $fBot.Dispose()

        # Left base facet (Mid reflection)
        $fLeft = New-Object System.Drawing.Drawing2D.GraphicsPath
        $fLeft.AddLine($pTop, $pBottom)
        $fLeft.AddLine($pBottom, $pCenter)
        $fLeft.CloseFigure()
        $bLeft = New-Object System.Drawing.SolidBrush($blueMid)
        $g.FillPath($bLeft, $fLeft)
        $bLeft.Dispose(); $fLeft.Dispose()

        # Refined seam & edge pen
        $edgePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(80, 255, 255, 255), [Math]::Max(0.5, [float]($s * 0.015)))
        $g.DrawLine($edgePen, $pCenter, $pRight)
        $edgePen.Dispose()
    }

    $framePen.Dispose()
    $baseBrush.Dispose()
    $path.Dispose()
    $g.Dispose()
    return $bmp
}

# Encode each frame as PNG, then assemble a standard multi-frame .ico (ICONDIR + entries + PNG blobs).
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Frame $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,($ms.ToArray())
    $ms.Dispose(); $bmp.Dispose()
}

$dir = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($OutPath))
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

$targetFile = (Resolve-Path -LiteralPath $dir).Path + "\" + [System.IO.Path]::GetFileName($OutPath)
$fs = [System.IO.File]::Create($targetFile)
$bw = New-Object System.IO.BinaryWriter($fs)
$count = $sizes.Count
$bw.Write([UInt16]0)      # reserved
$bw.Write([UInt16]1)      # type = icon
$bw.Write([UInt16]$count) # image count

$offset = 6 + 16 * $count
for ($i = 0; $i -lt $count; $i++) {
    $s = $sizes[$i]
    $len = $pngs[$i].Length
    $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s })))  # width  (0 => 256)
    $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s })))  # height (0 => 256)
    $bw.Write([Byte]0)         # palette count
    $bw.Write([Byte]0)         # reserved
    $bw.Write([UInt16]1)       # color planes
    $bw.Write([UInt16]32)      # bits per pixel
    $bw.Write([UInt32]$len)    # bytes in resource
    $bw.Write([UInt32]$offset) # offset
    $offset += $len
}
foreach ($png in $pngs) { $bw.Write($png) }
$bw.Flush(); $bw.Close(); $fs.Close()
Write-Host ("Wrote {0} ({1} frames)" -f $OutPath, $count)

# Also ensure packaging/TBD-Movies.ico is kept in sync
$pkgIconPath = "$PSScriptRoot/TBD-Movies.ico"
if ($OutPath -ne $pkgIconPath) {
    Copy-Item -Path $targetFile -Destination $pkgIconPath -Force
    Write-Host ("Synced to {0}" -f $pkgIconPath)
}
