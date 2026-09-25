Add-Type -AssemblyName System.Drawing

# find the DSH launcher shortcut without using non-ASCII literals
$lnkItem = Get-ChildItem 'C:\Users\cober\Desktop' -Filter 'DSH*.lnk' | Select-Object -First 1
if (-not $lnkItem) { Write-Output 'NO_LNK_FOUND'; exit 1 }
$lnk = $lnkItem.FullName
Write-Output ("lnk             : " + $lnk)

$shell = New-Object -ComObject WScript.Shell
$sc = $shell.CreateShortcut($lnk)

Write-Output ("TargetPath      : " + $sc.TargetPath)
Write-Output ("Arguments       : " + $sc.Arguments)
Write-Output ("WorkingDirectory: " + $sc.WorkingDirectory)
Write-Output ("IconLocation    : " + $sc.IconLocation)

$iconPath = $sc.IconLocation
$iconIndex = 0
if ($iconPath -and $iconPath.Contains(',')) {
    $parts = $iconPath.Split(',')
    $iconPath = $parts[0].Trim('"')
    $iconIndex = [int]$parts[1]
}
if (-not $iconPath -or -not (Test-Path $iconPath)) {
    $iconPath = $sc.TargetPath
    $iconIndex = 0
}
Write-Output ("IconSource      : " + $iconPath)
Write-Output ("IconIndex       : " + $iconIndex)
Write-Output ("IconSourceExists: " + (Test-Path $iconPath))

$outDir = 'C:\Users\cober\.openclaw\workspace\projects\ClassroomBreakLock\preview'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

foreach ($size in 256, 64, 32, 16) {
    try {
        if ($iconPath -match '\.ico$') {
            $icon = New-Object System.Drawing.Icon ($iconPath, $size, $size)
        } elseif ($size -gt 32) {
            $icon = New-Object System.Drawing.Icon ($iconPath, $size, $size)
        } else {
            $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($iconPath)
        }
        $bmp = $icon.ToBitmap()
        $bmp.Save((Join-Path $outDir ("dsh_icon_" + $size + ".png")), [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Output ("saved " + $size + " -> " + $icon.Width + "x" + $icon.Height)
        $bmp.Dispose()
        $icon.Dispose()
    } catch {
        Write-Output ("fail at " + $size + ": " + $_.Exception.Message)
    }
}
