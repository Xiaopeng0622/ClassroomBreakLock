$root = '$PSScriptRoot\..\src\ClassroomBreakLock'
Set-Location $root

Write-Output '===== 1) remaining MessageBox / system dialogs ====='
Get-ChildItem -Recurse -File -Include *.cs,*.xaml |
    Where-Object { $_.FullName -notmatch '\\obj\\' } |
    Select-String -Pattern 'MessageBox|SystemSounds|MessageBeep|SoundPlayer' |
    ForEach-Object { Write-Output ("{0}:{1}: {2}" -f $_.Filename, $_.LineNumber, $_.Line.Trim()) }

Write-Output ''
Write-Output '===== 2) SystemSoundsEx definition ====='
Get-ChildItem -Recurse -File -Include *.cs |
    Where-Object { $_.FullName -notmatch '\\obj\\' } |
    Select-String -Pattern 'class SystemSoundsEx' -Context 0,40 |
    ForEach-Object { $_.Context.PostContext } | ForEach-Object { Write-Output $_ }

Write-Output ''
Write-Output '===== 3) OnClassDismissed ====='
Get-ChildItem -Recurse -File -Include *.cs |
    Where-Object { $_.FullName -notmatch '\\obj\\' } |
    Select-String -Pattern 'private void OnClassDismissed' -Context 0,45 |
    ForEach-Object { Write-Output $_.Line; $_.Context.PostContext | ForEach-Object { Write-Output $_ } }
