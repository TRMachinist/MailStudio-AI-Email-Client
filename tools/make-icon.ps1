# MailStudio icon: blue background, white envelope, yellow AI star at the top right. Produces a multi-size .ico.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\MailStudio\Resources\MailStudio.ico'
$sizes = 16, 24, 32, 48, 64, 256
$pngs = @()
foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
  $r = [single]($s * 0.18)
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $w = [single]($s - 1)
  $path.AddArc(0, 0, $r*2, $r*2, 180, 90); $path.AddArc($w - $r*2, 0, $r*2, $r*2, 270, 90)
  $path.AddArc($w - $r*2, $w - $r*2, $r*2, $r*2, 0, 90); $path.AddArc(0, $w - $r*2, $r*2, $r*2, 90, 90); $path.CloseFigure()
  $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Point 0,0), (New-Object System.Drawing.Point $s,$s), [System.Drawing.Color]::FromArgb(0,120,212), [System.Drawing.Color]::FromArgb(0,84,166))
  $g.FillPath($bg, $path)
  # zarf
  $ex = $s*0.17; $ey = $s*0.30; $ew = $s*0.66; $eh = $s*0.46
  $penW = [Math]::Max(1.2, $s*0.055)
  $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
  $g.FillRectangle($white, [single]$ex, [single]$ey, [single]$ew, [single]$eh)
  $blue = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(0,90,180)), $penW
  $g.DrawLines($blue, @((New-Object System.Drawing.PointF ($ex+$penW/2), ($ey+$penW/2)), (New-Object System.Drawing.PointF ($ex+$ew/2), ($ey+$eh*0.58)), (New-Object System.Drawing.PointF ($ex+$ew-$penW/2), ($ey+$penW/2))))
  # star (AI)
  if ($s -ge 24) {
    $cx = $s*0.80; $cy = $s*0.22; $R = $s*0.17; $ri = $R*0.32
    $pts = for ($i=0; $i -lt 8; $i++) { $a = [Math]::PI/4*$i - [Math]::PI/2; $rr = if ($i % 2 -eq 0) { $R } else { $ri }; New-Object System.Drawing.PointF ([single]($cx + $rr*[Math]::Cos($a))), ([single]($cy + $rr*[Math]::Sin($a))) }
    $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,200,40))), $pts)
  }
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $pngs += ,($ms.ToArray())
  if ($s -eq 256) { $bmp.Save((Join-Path $PSScriptRoot '..\src\MailStudio\Resources\MailStudio.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
  $bmp.Dispose()
}
$fs = [System.IO.File]::Create($out); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i=0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $d = $pngs[$i]
  $bw.Write([byte]($(if ($s -ge 256) {0} else {$s}))); $bw.Write([byte]($(if ($s -ge 256) {0} else {$s})))
  $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
  $bw.Write([UInt32]$d.Length); $bw.Write([UInt32]$offset); $offset += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
$bw.Close()
"ikon: $out"
