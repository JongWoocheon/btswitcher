# Rasterize the project's original audio switch SVG paths for the Windows package.
# Uses Windows' WPF renderer; no image tools or extra packages are required.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore
$assets = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/BtSwitcher.Extension/Assets'

function Read-AudioSwitchDrawing([string] $Name) {
    $svg = [xml](Get-Content -LiteralPath (Join-Path $assets $Name) -Raw)
    $path = $svg.DocumentElement.SelectSingleNode('*[local-name()="path"]')
    $geometry = [System.Windows.Media.PathGeometry]::CreateFromGeometry(
        [System.Windows.Media.Geometry]::Parse($path.GetAttribute('d')))
    $geometry.FillRule = [System.Windows.Media.FillRule]::Nonzero
    $fill = if ($path.HasAttribute('fill')) { $path.GetAttribute('fill') } else { '#000000' }
    $brush = [System.Windows.Media.BrushConverter]::new().ConvertFromString($fill)
    return [System.Windows.Media.GeometryDrawing]::new($brush, $null, $geometry)
}

# The suffix names the background theme, not the color of the artwork.
$onDarkBackground = Read-AudioSwitchDrawing 'audio-switch-dark.svg'
$onLightBackground = Read-AudioSwitchDrawing 'audio-switch.svg'

function Write-Logo([string] $Name, [int] $Width, [int] $Height, [double] $LogoSize, $Drawing) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    try {
        $context.PushTransform([System.Windows.Media.TranslateTransform]::new(
            ($Width - $LogoSize) / 2, ($Height - $LogoSize) / 2))
        # Both source SVGs use a 24 x 24 viewBox.
        $context.PushTransform([System.Windows.Media.ScaleTransform]::new($LogoSize / 24, $LogoSize / 24))
        $context.DrawDrawing($Drawing)
        $context.Pop()
        $context.Pop()
    }
    finally { $context.Close() }

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $Width, $Height, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.File]::Create((Join-Path $assets $Name))
    try { $encoder.Save($stream) }
    finally { $stream.Dispose() }
}

# Theme-specific, transparent app-list / search / taskbar icons.
foreach ($size in 16,20,24,30,32,36,40,48,60,64,72,80,96,256) {
    Write-Logo "Square44x44Logo.targetsize-$size.png" $size $size $size $onDarkBackground
    Write-Logo "Square44x44Logo.targetsize-${size}_altform-unplated.png" $size $size $size $onDarkBackground
    Write-Logo "Square44x44Logo.targetsize-${size}_altform-lightunplated.png" $size $size $size $onLightBackground
}

foreach ($scale in 100,125,150,200,400) {
    $appSize = [int][Math]::Round(44 * $scale / 100)
    $tileSize = [int][Math]::Round(150 * $scale / 100)
    Write-Logo "Square44x44Logo.scale-$scale.png" $appSize $appSize $appSize $onDarkBackground
    Write-Logo "Square150x150Logo.scale-$scale.png" $tileSize $tileSize ($tileSize * 2 / 3) $onDarkBackground
}

# Replace the remaining template artwork referenced by the manifest.
Write-Logo 'StoreLogo.png' 50 50 50 $onDarkBackground
Write-Logo 'Wide310x150Logo.scale-200.png' 620 300 200 $onDarkBackground
Write-Logo 'SplashScreen.scale-200.png' 1240 600 200 $onDarkBackground
Write-Logo 'LockScreenLogo.scale-200.png' 48 48 48 $onDarkBackground
Write-Host 'Generated Windows package icons from the audio switch SVG assets.'
