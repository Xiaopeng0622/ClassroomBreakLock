$f = 'C:\Users\cober\.openclaw\media\inbound\26.3.18---d5f070aa-7047-400d-a0c7-a5e2087301cd.yaml'

Write-Output "===== 总行数 / 大小 ====="
(Get-Content $f).Count
(Get-Item $f).Length

Write-Output ''
Write-Output "===== schedules 段的骨架（name / enable_day / weeks / subject 计数）====="
$lines = Get-Content $f -Encoding UTF8
for ($i = 0; $i -lt $lines.Count; $i++) {
    $l = $lines[$i]
    if ($l -match '^\s{0,2}(schedules|subjects|version|classes)\s*:') {
        Write-Output ("{0,4}: [{1}] {2}" -f ($i + 1), $l.Length - $l.TrimStart().Length, $l.TrimEnd())
    }
    elseif ($l -match '^\s*-?\s*(name|enable_day|weeks|subject)\s*:') {
        Write-Output ("{0,4}: [{1}] {2}" -f ($i + 1), $l.Length - $l.TrimStart().Length, $l.TrimEnd())
    }
}

Write-Output ''
Write-Output "===== 顶层结构（只看缩进<=2 的行）====="
for ($i = 0; $i -lt $lines.Count; $i++) {
    $l = $lines[$i]
    if ($l.Trim().Length -eq 0) { continue }
    $indent = $l.Length - $l.TrimStart().Length
    if ($indent -le 2) {
        Write-Output ("{0,4}: {1}" -f ($i + 1), $l.TrimEnd())
    }
}
