$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskAssets = Join-Path (Split-Path $PSScriptRoot -Parent) 'NetEaseNEXT\Assets'
# Independently redrawn vector geometry. Keep in sync with AppMark.svg.
$taskCurve = [Drawing.Drawing2D.GraphicsPath]::new()
$taskCurve.StartFigure()
$taskCurve.AddBezier(208,133,126,164,97,250,130,321)
$taskCurve.AddBezier(130,321,158,383,236,409,298,377)
$taskCurve.AddBezier(298,377,371,344,401,267,359,215)
$taskCurve.AddBezier(359,215,326,174,278,177,247,193)
$taskCurve.AddBezier(247,193,210,211,202,246,224,276)
$taskCurve.AddBezier(224,276,244,305,282,292,288,262)
$taskCurve.AddBezier(288,262,293,240,275,198,263,150)
$taskCurve.AddBezier(263,150,250,103,271,91,289,91)
$taskCurve.AddBezier(289,91,304,90,317,97,327,106)
try {
    foreach ($taskName in @('AppIcon','AppIconDark')) {
        $taskColor = if ($taskName -eq 'AppIconDark') { [Drawing.Color]::White } else { [Drawing.Color]::FromArgb(255,32,32,32) }
        $taskImages = [Collections.Generic.List[byte[]]]::new()
        $taskSizes = @(256,64,48,32,24,16)
        foreach ($taskSize in $taskSizes) {
            # Supersampling keeps the original vector smooth even at 16 pixels.
            $taskLarge = [Drawing.Bitmap]::new(1024,1024,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $taskDraw = [Drawing.Graphics]::FromImage($taskLarge)
            $taskPen = [Drawing.Pen]::new($taskColor,36)
            try {
                $taskDraw.Clear([Drawing.Color]::Transparent)
                $taskDraw.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
                $taskDraw.ScaleTransform(2,2)
                $taskPen.StartCap = $taskPen.EndCap = [Drawing.Drawing2D.LineCap]::Round
                $taskPen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
                $taskDraw.DrawPath($taskPen,$taskCurve)
                if ($taskSize -eq 256) { $taskLarge.Save((Join-Path $taskAssets "$taskName.png"),[Drawing.Imaging.ImageFormat]::Png) }
                $taskBitmap = [Drawing.Bitmap]::new($taskSize,$taskSize,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
                $taskGraphics = [Drawing.Graphics]::FromImage($taskBitmap)
                $taskMemory = [IO.MemoryStream]::new()
                try {
                    $taskGraphics.Clear([Drawing.Color]::Transparent)
                    $taskGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                    $taskGraphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                    $taskGraphics.DrawImage($taskLarge,[Drawing.Rectangle]::new(0,0,$taskSize,$taskSize))
                    $taskBitmap.Save($taskMemory,[Drawing.Imaging.ImageFormat]::Png); $taskImages.Add($taskMemory.ToArray())
                } finally { $taskMemory.Dispose(); $taskGraphics.Dispose(); $taskBitmap.Dispose() }
            } finally { $taskPen.Dispose(); $taskDraw.Dispose(); $taskLarge.Dispose() }
        }
        $taskFile = [IO.File]::Create((Join-Path $taskAssets "$taskName.ico"))
        $taskWriter = [IO.BinaryWriter]::new($taskFile)
        try {
            $taskWriter.Write([uint16]0); $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]$taskSizes.Count)
            $taskOffset = 6 + 16*$taskSizes.Count
            for ($taskIndex=0; $taskIndex -lt $taskSizes.Count; $taskIndex++) {
                $taskDimension = if ($taskSizes[$taskIndex] -eq 256) { 0 } else { $taskSizes[$taskIndex] }
                $taskWriter.Write([byte]$taskDimension); $taskWriter.Write([byte]$taskDimension)
                $taskWriter.Write([byte]0); $taskWriter.Write([byte]0); $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]32)
                $taskWriter.Write([uint32]$taskImages[$taskIndex].Length); $taskWriter.Write([uint32]$taskOffset)
                $taskOffset += $taskImages[$taskIndex].Length
            }
            foreach ($taskImage in $taskImages) { $taskWriter.Write($taskImage) }
        } finally { $taskWriter.Dispose(); $taskFile.Dispose() }
    }
} finally { $taskCurve.Dispose() }
