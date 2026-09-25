Add-Type -AssemblyName System.Drawing

$outDir = 'C:\Users\cober\.openclaw\workspace\projects\ClassroomBreakLock\preview'
$exes = @(
    'D:\openclaw\tools\dsh-launcher\DeepSeekHarness-v1.1.3.exe',
    'D:\openclaw\tools\dsh-launcher\DeepSeekHarness-v1.1.4.exe',
    'D:\openclaw\tools\dsh-launcher\DeepSeekHarness.exe'
)

foreach ($exe in $exes) {
    if (-not (Test-Path $exe)) { continue }
    $tag = [System.IO.Path]::GetFileNameWithoutExtension($exe)
    foreach ($size in 256, 128, 64, 48, 32, 24, 16) {
        try {
            $icon = New-Object System.Drawing.Icon ($exe, $size, $size)
            $bmp = $icon.ToBitmap()
            $bmp.Save((Join-Path $outDir ("whale_" + $tag + "_" + $size + ".png")), [System.Drawing.Imaging.ImageFormat]::Png)
            Write-Output ($tag + " " + $size + " -> " + $icon.Width + "x" + $icon.Height)
            $bmp.Dispose(); $icon.Dispose()
        } catch {
            Write-Output ($tag + " " + $size + " FAIL: " + $_.Exception.Message)
        }
    }
}
