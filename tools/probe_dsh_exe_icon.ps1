Add-Type -AssemblyName System.Drawing

$exe = 'D:\openclaw\tools\dsh-launcher\DeepSeekHarness-v1.1.3.exe'
$outDir = 'C:\Users\cober\.openclaw\workspace\projects\ClassroomBreakLock\preview'

Write-Output ("exe exists: " + (Test-Path $exe))
Get-ChildItem 'D:\openclaw\tools\dsh-launcher' -File | Select-Object Name, Length | Format-Table -AutoSize | Out-String -Width 120

try {
    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($exe)
    $bmp = $icon.ToBitmap()
    $bmp.Save((Join-Path $outDir 'dsh_exe_icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Output ("exe icon saved: " + $icon.Width + "x" + $icon.Height)
    $bmp.Dispose(); $icon.Dispose()
} catch {
    Write-Output ("exe icon fail: " + $_.Exception.Message)
}
