Add-Type -AssemblyName System.Drawing

function Draw-Icon([System.Drawing.Graphics]$g, [int]$S) {
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $u = $S / 256.0

    # ---- rounded-square paper background (warm cream gradient) ----
    $pad = 8 * $u
    $rect = New-Object System.Drawing.RectangleF($pad,$pad,($S-2*$pad),($S-2*$pad))
    $rad = 56 * $u
    $d = $rad * 2
    $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bg.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $bg.AddArc($rect.Right-$d, $rect.Y, $d, $d, 270, 90)
    $bg.AddArc($rect.Right-$d, $rect.Bottom-$d, $d, $d, 0, 90)
    $bg.AddArc($rect.X, $rect.Bottom-$d, $d, $d, 90, 90)
    $bg.CloseFigure()
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, [System.Drawing.Color]::FromArgb(255,252,249,242), [System.Drawing.Color]::FromArgb(255,237,228,212), 90)
    $g.FillPath($grad, $bg)

    # subtle inner border
    $bpen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60,150,135,110), (2.5*$u))
    $g.DrawPath($bpen, $bg)

    # ---- faint ruled paper lines ----
    $lpen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(45,120,105,80), (2.2*$u))
    foreach ($ly in 158,190,222) { $g.DrawLine($lpen, (52*$u),($ly*$u), (204*$u),($ly*$u)) }

    # ---- orange ink stroke being written (the "writing") ----
    $ink = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255,217,119,87), (10*$u))
    $ink.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $ink.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $sq = New-Object System.Drawing.Drawing2D.GraphicsPath
    $sq.AddBezier( (58*$u),(190*$u), (84*$u),(168*$u), (104*$u),(206*$u), (132*$u),(188*$u) )
    $g.DrawPath($ink, $sq)

    # ---- quill feather (dark, tilted top-right -> nib at lower-left) ----
    $nibX = 132*$u; $nibY = 188*$u     # nib tip touches the ink stroke
    $topX = 210*$u; $topY = 44*$u      # plume top
    $feather = New-Object System.Drawing.Drawing2D.GraphicsPath
    $feather.AddBezier($nibX,$nibY, (138*$u),(140*$u), (168*$u),(80*$u), $topX,$topY)
    $feather.AddBezier($topX,$topY, (224*$u),(92*$u), (196*$u),(150*$u), $nibX,$nibY)
    $feather.CloseFigure()
    $fbrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255,38,37,35))
    $g.FillPath($fbrush, $feather)

    # feather spine (light) + a couple of barb notches for the cartoon look
    $spine = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(235,250,249,245), (4*$u))
    $spine.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $spine.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($spine, $nibX,$nibY, (198*$u),(64*$u))
    $barb = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(150,250,249,245), (2.6*$u))
    $g.DrawLine($barb, (160*$u),(128*$u), (188*$u),(120*$u))
    $g.DrawLine($barb, (172*$u),(104*$u), (198*$u),(96*$u))
    $g.DrawLine($barb, (150*$u),(150*$u), (176*$u),(146*$u))

    # nib accent (brand orange tip)
    $tip = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255,217,119,87), (8*$u))
    $tip.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $tip.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($tip, $nibX,$nibY, (146*$u),(166*$u))

    $bpen.Dispose(); $lpen.Dispose(); $ink.Dispose(); $fbrush.Dispose(); $spine.Dispose(); $barb.Dispose(); $tip.Dispose(); $grad.Dispose()
}

function New-Bmp([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap($S,$S,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    Draw-Icon $g $S
    $g.Dispose()
    return $bmp
}

$dir = 'C:\Users\irony\Downloads\New folder (2)\LectureInk\src\LectureInk\Assets'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$icoPath = Join-Path $dir 'app.ico'
$preview = 'C:\Users\irony\Downloads\New folder (2)\LectureInk\tools\icon_preview.png'

# preview at 256
$pv = New-Bmp 256
$pv.Save($preview, [System.Drawing.Imaging.ImageFormat]::Png)
$pv.Dispose()

# build multi-size ICO with PNG-encoded entries
$sizes = 256,128,64,48,32,16
$blobs = @()
foreach ($s in $sizes) {
    $b = New-Bmp $s
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $blobs += ,($ms.ToArray())
    $ms.Dispose(); $b.Dispose()
}
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16*$sizes.Count
for ($i=0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $blobs[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($blob in $blobs) { $bw.Write($blob) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $out.ToArray())
$bw.Dispose(); $out.Dispose()

Write-Output "ICO bytes: $((Get-Item $icoPath).Length)"
Write-Output "Preview: $preview"
