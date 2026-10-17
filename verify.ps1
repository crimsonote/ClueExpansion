# verify.ps1 —— 提交前的机械检查（BOM / 行尾 / 构建）
#
# 用法：pwsh -File verify.ps1

$ErrorActionPreference = "Stop"
$fail = 0

function Fail($msg) {
    Write-Host ("  FAIL  " + $msg) -ForegroundColor Red
    $script:fail++
}

Write-Host "[1/4] UTF-8 BOM 检查（.cs / .ps1）"
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
$files = Get-ChildItem -Path $PSScriptRoot -Recurse -Include *.cs, *.ps1 -File |
    Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' }
foreach ($f in $files) {
    $b = [System.IO.File]::ReadAllBytes($f.FullName)
    if (-not ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)) {
        [System.IO.File]::WriteAllText($f.FullName, [System.Text.Encoding]::UTF8.GetString($b), $utf8Bom)
        Write-Host ("  已补 BOM: " + $f.FullName.Replace($PSScriptRoot + '\', '')) -ForegroundColor Yellow
    }
}
Write-Host ("  共 " + $files.Count + " 个文件")

Write-Host "[2/4] 乱码哨兵检查（无 BOM 时 Roslyn 按 GBK 读源码会产生的典型乱码）"
$sentinels = @('鍋', '濡', '锟')
foreach ($f in $files) {
    if ($f.Name -eq 'verify.ps1') { continue }   # 本脚本自身就写着这些哨兵
    $text = Get-Content $f.FullName -Encoding utf8 -Raw
    foreach ($s in $sentinels) {
        if ($text.Contains($s)) {
            Fail ($f.Name + " 含乱码哨兵 '" + $s + "'")
        }
    }
}

Write-Host "[3/4] 禁止项检查"
foreach ($f in $files) {
    $text = Get-Content $f.FullName -Encoding utf8 -Raw
    if ($text -match 'Debug\.Log\(') {
        Fail ($f.Name + " 使用了 UnityEngine.Debug.Log（应走 Core.Log）")
    }
}

Write-Host "[4/4] 构建"
Push-Location $PSScriptRoot
try {
    $out = & dotnet build -c Release --nologo 2>&1 | Out-String
    if ($out -match 'error|warning') {
        $lines = ($out -split "`n") | Where-Object { $_ -match 'error|warning' }
        $lines | Select-Object -First 20 | ForEach-Object { Write-Host ("  " + $_.Trim()) }
        Fail "构建有错误或警告"
    }
} finally {
    Pop-Location
}

if ($fail -gt 0) {
    Write-Host ("verify FAIL: " + $fail + " 项") -ForegroundColor Red
    exit 1
}
Write-Host "verify PASS" -ForegroundColor Green
