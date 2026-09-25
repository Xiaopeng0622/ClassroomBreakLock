$proj = '$PSScriptRoot\..\src\ClassroomBreakLock\ClassroomBreakLock.csproj'

foreach ($cfg in @('Debug', 'Release')) {
    Write-Output "===== build $cfg ====="
    dotnet build $proj -c $cfg -v quiet -nologo 2>&1 |
        Where-Object { $_ -match ': error|: warning CS' } |
        Select-Object -First 12 |
        ForEach-Object { Write-Output $_ }
    Write-Output "exitcode=$LASTEXITCODE"
}

Write-Output ''
Write-Output '===== 产物时间戳 ====='
Get-ChildItem '$PSScriptRoot\..\src\ClassroomBreakLock\bin\*\net8.0-windows\ClassroomBreakLock.exe' |
    Select-Object FullName, Length, LastWriteTime | Format-Table -AutoSize | Out-String -Width 160

Write-Output '===== 残留系统弹窗 / 提示音 ====='
$root = '$PSScriptRoot\..\src\ClassroomBreakLock'
Get-ChildItem $root -Recurse -File -Include *.cs,*.xaml |
    Where-Object { $_.FullName -notmatch '\\obj\\' } |
    Select-String -Pattern 'MessageBox\.Show|SystemSounds|MessageBeep|SoundPlayer' |
    ForEach-Object { Write-Output ("{0}:{1}: {2}" -f $_.Filename, $_.LineNumber, $_.Line.Trim()) }
Write-Output '(以上为空即表示已全部替换)'
