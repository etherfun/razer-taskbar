param([string]$In, [string]$Out, [int]$Factor = 4)
Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Bitmap]::FromFile($In)
$w = $src.Width; $h = $src.Height
$big = New-Object System.Drawing.Bitmap(([int]$w * $Factor), ([int]$h * $Factor))
$g = [System.Drawing.Graphics]::FromImage($big)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$g.DrawImage($src, 0, 0, ([int]$w * $Factor), ([int]$h * $Factor))
$g.Dispose()
$big.Save($Out)
$src.Dispose(); $big.Dispose()
Write-Output 'saved'
