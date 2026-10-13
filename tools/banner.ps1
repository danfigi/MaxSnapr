# Builds a 1280x640 promo banner from the logo and self-test screenshots (synthetic content only).
param([string]$Logo, [string]$Editor, [string]$Setup, [string]$Out)
Add-Type -AssemblyName System.Drawing
$W = 1280; $H = 640
$bmp = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'AntiAliasGridFit'

$rect = New-Object System.Drawing.Rectangle 0, 0, $W, $H
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(0x1A, 0x1C, 0x22)), ([System.Drawing.Color]::FromArgb(0x2A, 0x1E, 0x2A)), 35
$g.FillRectangle($bg, $rect)
# Warm glow behind the logo, in the brand colours.
$glow = New-Object System.Drawing.Drawing2D.GraphicsPath
$glow.AddEllipse(-220, -180, 760, 760)
$pg = New-Object System.Drawing.Drawing2D.PathGradientBrush $glow
$pg.CenterColor = [System.Drawing.Color]::FromArgb(90, 0xFF, 0x8A, 0x4C)
$pg.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 0xFF, 0x8A, 0x4C))
$g.FillPath($pg, $glow)

# Draws an image (optionally only its top $cropH source pixels) with a soft shadow.
function Shadowed($img, [int]$x, [int]$y, [int]$w, [int]$cropH = 0) {
  $srcH = if ($cropH -gt 0) { $cropH } else { $img.Height }
  $h = [int]($srcH * $w / $img.Width)
  for ($i = 14; $i -ge 1; $i -= 2) {
    $sb = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(10, 0, 0, 0))
    $g.FillRectangle($sb, $x - $i + 10, $y - $i + 16, $w + 2 * $i, $h + 2 * $i)
  }
  $dest = New-Object System.Drawing.Rectangle $x, $y, $w, $h
  $g.DrawImage($img, $dest, 0, 0, $img.Width, $srcH, [System.Drawing.GraphicsUnit]::Pixel)
  $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(60, 255, 255, 255)), 1
  $g.DrawRectangle($pen, $x, $y, $w, $h)
}

$imgEditor = [System.Drawing.Image]::FromFile($Editor)
$imgSetup = [System.Drawing.Image]::FromFile($Setup)
Shadowed $imgEditor 560 110 680
# Only the button row: the status line under it names the local microphone.
Shadowed $imgSetup 600 520 560 56

$imgLogo = [System.Drawing.Image]::FromFile($Logo)
$g.DrawImage($imgLogo, 70, 80, 150, 150)
$white = [System.Drawing.Brushes]::White
$soft = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0xC9, 0xCE, 0xD6))
$accent = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0xFF, 0xB1, 0x3D))
$g.DrawString("MaxSnapr", (New-Object System.Drawing.Font "Segoe UI Semibold", 54), $white, 62, 250)
$g.DrawString("Captura, anota y graba tu pantalla", (New-Object System.Drawing.Font "Segoe UI", 22), $soft, 70, 345)
$y = 410
foreach ($line in @("Editor con flechas, globos, pasos y difuminado", "Vídeo con audio, cámara y fondos virtuales", "OCR, captura con desplazamiento y biblioteca")) {
  $g.FillEllipse($accent, 72, $y + 11, 9, 9)
  $g.DrawString($line, (New-Object System.Drawing.Font "Segoe UI", 16), $soft, 92, $y)
  $y += 38
}
$g.DrawString("Para Windows · Gratis · Sin anuncios", (New-Object System.Drawing.Font "Segoe UI Semibold", 15), $accent, 70, 545)

$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose(); $imgEditor.Dispose(); $imgSetup.Dispose(); $imgLogo.Dispose()
