param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskAssets = Join-Path (Split-Path $PSScriptRoot -Parent) 'NetEaseNEXT\Assets'
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$taskDark = [Drawing.Image]::FromFile((Join-Path $taskAssets 'AppIconDark.png'))
$taskLight = [Drawing.Image]::FromFile((Join-Path $taskAssets 'AppIcon.png'))
function Write-PackageIcon([Drawing.Image]$Image, [int]$Size, [string]$Name) {
    $taskBitmap = [Drawing.Bitmap]::new($Size,$Size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $taskGraphics = [Drawing.Graphics]::FromImage($taskBitmap)
    try {
        $taskGraphics.Clear([Drawing.Color]::Transparent)
        $taskGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $taskGraphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $taskGraphics.DrawImage($Image,[Drawing.Rectangle]::new(0,0,$Size,$Size))
        $taskBitmap.Save((Join-Path $Destination $Name),[Drawing.Imaging.ImageFormat]::Png)
    } finally { $taskGraphics.Dispose(); $taskBitmap.Dispose() }
}
try {
    foreach($taskSpec in @(@('AppList',44),@('Square150x150Logo',150),@('StoreLogo',50))) {
        foreach($taskScale in @(100,125,150,200,250,300,400)) {
            $taskSize = [int][math]::Ceiling([int]$taskSpec[1] * $taskScale / 100)
            Write-PackageIcon $taskDark $taskSize "$($taskSpec[0]).scale-$taskScale.png"
            Write-PackageIcon $taskLight $taskSize "$($taskSpec[0]).scale-${taskScale}_altform-colorful_theme-light.png"
        }
    }
    foreach($taskSize in @(16,20,24,30,32,36,40,44,48,60,64,72,80,96,256)) {
        Write-PackageIcon $taskDark $taskSize "AppList.targetsize-$taskSize.png"
        Write-PackageIcon $taskDark $taskSize "AppList.targetsize-${taskSize}_altform-unplated.png"
        Write-PackageIcon $taskLight $taskSize "AppList.targetsize-${taskSize}_altform-lightunplated.png"
    }
} finally { $taskDark.Dispose(); $taskLight.Dispose() }
