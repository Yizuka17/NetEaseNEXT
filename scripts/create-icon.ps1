$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskAssets = Join-Path (Split-Path $PSScriptRoot -Parent) 'NetEaseNEXT\Assets'
New-Item -ItemType Directory -Path $taskAssets -Force | Out-Null
$taskImages = [Collections.Generic.List[byte[]]]::new()
foreach ($taskSize in @(256,48,32,16)) {
    $taskBitmap = [Drawing.Bitmap]::new($taskSize,$taskSize)
    $taskGraphics = [Drawing.Graphics]::FromImage($taskBitmap)
    try {
        $taskGraphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $taskGraphics.ScaleTransform($taskSize/256.0,$taskSize/256.0)
        $taskBrush = [Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.Point]::new(0,0),[Drawing.Point]::new(256,256),[Drawing.Color]::FromArgb(255,228,66,94),[Drawing.Color]::FromArgb(255,161,34,73))
        try { $taskGraphics.FillEllipse($taskBrush,8,8,240,240) } finally { $taskBrush.Dispose() }
        $taskPen = [Drawing.Pen]::new([Drawing.Color]::White,15)
        $taskPen.StartCap = $taskPen.EndCap = [Drawing.Drawing2D.LineCap]::Round
        try {
            $taskGraphics.DrawLine($taskPen,108,167,108,83)
            $taskGraphics.DrawLine($taskPen,108,83,180,65)
            $taskGraphics.DrawLine($taskPen,180,65,180,148)
        } finally { $taskPen.Dispose() }
        $taskGraphics.FillEllipse([Drawing.Brushes]::White,67,153,49,36)
        $taskGraphics.FillEllipse([Drawing.Brushes]::White,139,134,49,36)
        $taskMemory = [IO.MemoryStream]::new()
        try { $taskBitmap.Save($taskMemory,[Drawing.Imaging.ImageFormat]::Png); $taskImages.Add($taskMemory.ToArray()) } finally { $taskMemory.Dispose() }
    } finally { $taskGraphics.Dispose(); $taskBitmap.Dispose() }
}
$taskFile = [IO.File]::Create((Join-Path $taskAssets 'AppIcon.ico'))
$taskWriter = [IO.BinaryWriter]::new($taskFile)
try {
    $taskWriter.Write([uint16]0); $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]4)
    $taskOffset = 6 + 16*4
    $taskSizes = @(0,48,32,16)
    for ($taskIndex=0; $taskIndex -lt 4; $taskIndex++) {
        $taskWriter.Write([byte]$taskSizes[$taskIndex]); $taskWriter.Write([byte]$taskSizes[$taskIndex])
        $taskWriter.Write([byte]0); $taskWriter.Write([byte]0); $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]32)
        $taskWriter.Write([uint32]$taskImages[$taskIndex].Length); $taskWriter.Write([uint32]$taskOffset)
        $taskOffset += $taskImages[$taskIndex].Length
    }
    foreach ($taskImage in $taskImages) { $taskWriter.Write($taskImage) }
} finally { $taskWriter.Dispose(); $taskFile.Dispose() }
