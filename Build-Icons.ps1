param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'Assets'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
[void][IO.Directory]::CreateDirectory($OutputDirectory)
foreach ($kind in @('Client','Server')) {
    $bitmap = New-Object Drawing.Bitmap 256,256
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([Drawing.Color]::FromArgb(12,23,40))
    $pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(61,218,242)),12
    $white = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(226,245,255))
    try {
        $g.DrawPolygon($pen, [Drawing.Point[]]@([Drawing.Point]::new(128,20),[Drawing.Point]::new(222,74),[Drawing.Point]::new(222,182),[Drawing.Point]::new(128,236),[Drawing.Point]::new(34,182),[Drawing.Point]::new(34,74)))
        if ($kind -eq 'Server') {
            foreach ($y in @(76,116,156)) { $g.DrawRectangle($pen,74,$y,108,28); $g.FillEllipse($white,85,($y+9),10,10) }
        } else { $g.DrawRectangle($pen,67,75,122,88); $g.DrawLine($pen,128,168,128,191); $g.DrawLine($pen,96,194,160,194) }
        $png = New-Object IO.MemoryStream
        try {
            $bitmap.Save($png,[Drawing.Imaging.ImageFormat]::Png)
            $file = [IO.File]::Create((Join-Path $OutputDirectory ('UDA-'+$kind+'.ico')))
            $writer = New-Object IO.BinaryWriter $file
            try {
                $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
                $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0)
                $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$png.Length); $writer.Write([uint32]22)
                $writer.Write($png.ToArray())
            } finally { $writer.Dispose() }
        } finally { $png.Dispose() }
    } finally { $g.Dispose(); $bitmap.Dispose(); $pen.Dispose(); $white.Dispose() }
}
