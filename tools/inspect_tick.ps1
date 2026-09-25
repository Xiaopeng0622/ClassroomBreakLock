$root = 'C:\Users\cober\.openclaw\workspace\projects\ClassroomBreakLock\src\ClassroomBreakLock'
Set-Location $root

Write-Output '===== _manualUnlockUntil usage ====='
Get-ChildItem -Recurse -File -Include *.cs |
    Where-Object { $_.FullName -notmatch '\\obj\\' } |
    Select-String -Pattern '_manualUnlockUntil' -Context 3,3 |
    ForEach-Object {
        Write-Output ("--- {0}:{1}" -f $_.Filename, $_.LineNumber)
        $_.Context.PreContext | ForEach-Object { Write-Output ("    | " + $_) }
        Write-Output ("  > " + $_.Line)
        $_.Context.PostContext | ForEach-Object { Write-Output ("    | " + $_) }
    }

Write-Output ''
Write-Output '===== Tick method ====='
Get-ChildItem -Recurse -File -Include *.cs |
    Where-Object { $_.FullName -notmatch '\\obj\\' } |
    Select-String -Pattern 'private void Tick\(\)' -Context 0,45 |
    ForEach-Object { Write-Output $_.Line; $_.Context.PostContext | ForEach-Object { Write-Output $_ } }

Write-Output ''
Write-Output '===== README exists? ====='
Get-ChildItem 'C:\Users\cober\.openclaw\workspace\projects\ClassroomBreakLock' -File | Select-Object Name
