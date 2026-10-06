# Generates the installer wizard artwork (Linear-style: near-black canvas, indigo glow) from design\icon-master-1024.png.
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot
$icon = [System.Drawing.Image]::FromFile((Join-Path $root 'design\icon-master-1024.png'))
$cropSrc = New-Object System.Drawing.Rectangle 150,150,724,724

function New-Canvas($w,$h) {
    $bmp = New-Object System.Drawing.Bitmap $w,$h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode='AntiAlias'; $g.InterpolationMode='HighQualityBicubic'; $g.TextRenderingHint='AntiAliasGridFit'
    $g.Clear([System.Drawing.ColorTranslator]::FromHtml('#050506'))
    # ambient glow from the top, a purple pool bottom-left
    foreach ($b in @(@(0.5,-0.15,1.3,'#5E6AD2',70), @(0.1,1.0,1.0,'#A855F7',34))) {
        $r = [int]($w*$b[2]); $cx=[int]($w*$b[0]); $cy=[int]($h*$b[1])
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath; $path.AddEllipse($cx-$r,$cy-$r,2*$r,2*$r)
        $pg = New-Object System.Drawing.Drawing2D.PathGradientBrush $path
        $c=[System.Drawing.ColorTranslator]::FromHtml($b[3])
        $pg.CenterColor=[System.Drawing.Color]::FromArgb($b[4],$c); $pg.SurroundColors=@([System.Drawing.Color]::FromArgb(0,$c))
        $g.FillPath($pg,$path)
    }
    return @($bmp,$g)
}

foreach ($scale in 1,2) {
    $w=164*$scale; $h=314*$scale
    $bmp,$g = New-Canvas $w $h
    # hairline grid
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(7,255,255,255)),1
    for ($x=0; $x -lt $w; $x+=32*$scale) { $g.DrawLine($pen,$x,0,$x,$h) }
    for ($y=0; $y -lt $h; $y+=32*$scale) { $g.DrawLine($pen,0,$y,$w,$y) }
    $size=96*$scale
    $g.DrawImage($icon,(New-Object System.Drawing.Rectangle ([int](($w-$size)/2)),(40*$scale),$size,$size),$cropSrc,'Pixel')
    $f1 = New-Object System.Drawing.Font 'Segoe UI Semibold',(15*$scale),'Regular','Pixel'
    $f2 = New-Object System.Drawing.Font 'Segoe UI',(10*$scale),'Regular','Pixel'
    $sf = New-Object System.Drawing.StringFormat; $sf.Alignment='Center'
    $g.DrawString('DesktopUtility',$f1,(New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#EDEDEF'))),(New-Object System.Drawing.RectangleF 0,(150*$scale),$w,(30*$scale)),$sf)
    $g.DrawString("iPad як другий монітор`nдля Windows",$f2,(New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#8A8F98'))),(New-Object System.Drawing.RectangleF 0,(182*$scale),$w,(44*$scale)),$sf)
    $g.Dispose(); $bmp.Save((Join-Path $PSScriptRoot "wizard-$scale.bmp"),[System.Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()

    $s=55*$scale
    $bmp = New-Object System.Drawing.Bitmap $s,$s
    $g=[System.Drawing.Graphics]::FromImage($bmp); $g.InterpolationMode='HighQualityBicubic'; $g.Clear([System.Drawing.ColorTranslator]::FromHtml('#050506'))
    $g.DrawImage($icon,(New-Object System.Drawing.Rectangle 0,0,$s,$s),$cropSrc,'Pixel'); $g.Dispose()
    $bmp.Save((Join-Path $PSScriptRoot "small-$scale.bmp"),[System.Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()
}
$icon.Dispose()
