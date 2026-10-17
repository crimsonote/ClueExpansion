# deploy.ps1 —— 把最新构建复制到游戏插件目录
#
# 用法：
#   pwsh -File deploy.ps1
#   pwsh -File deploy.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Deadly Trick"

param(
    [string]$GameDir = $(if ($env:DEADLYTRICK_DIR) { $env:DEADLYTRICK_DIR } else { "C:\Program Files (x86)\Steam\steamapps\common\Deadly Trick" }),
    [switch]$SkipFreshnessCheck
)

$ErrorActionPreference = "Stop"

$src = Join-Path $PSScriptRoot "bin\Release\netstandard2.1\ClueDetail.dll"
if (-not (Test-Path $src)) {
    Write-Host "找不到构建产物: $src" -ForegroundColor Red
    Write-Host "请先执行: dotnet build -c Release" -ForegroundColor Yellow
    exit 1
}

$plugins = Join-Path $GameDir "BepInEx\plugins"
if (-not (Test-Path $plugins)) {
    Write-Host "找不到插件目录: $plugins" -ForegroundColor Red
    Write-Host "请用 -GameDir 指定游戏安装目录" -ForegroundColor Yellow
    exit 1
}

# ── 新鲜度检查 ────────────────────────────────────────────────────────
# 编译失败时旧 DLL 仍在，只检查"文件存在"会照样复制并打印"已部署"。
if (-not $SkipFreshnessCheck) {
    $dllTime = (Get-Item $src).LastWriteTimeUtc
    $newer = Get-ChildItem -Path $PSScriptRoot -Recurse -Filter *.cs |
        Where-Object { $_.FullName -notmatch '\\obj\\' -and $_.LastWriteTimeUtc -gt $dllTime }
    if ($newer) {
        Write-Host "构建产物比源文件旧，拒绝部署（请先 dotnet build -c Release）：" -ForegroundColor Red
        $newer | Select-Object -First 10 | ForEach-Object {
            Write-Host ("  " + $_.FullName.Replace($PSScriptRoot + '\', '') + "  " + $_.LastWriteTime)
        }
        exit 1
    }
}

$dst = Join-Path $plugins "ClueDetail.dll"

$running = Get-Process -Name 'DeadlyTrick*' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "游戏正在运行，ClueDetail.dll 被占用。请先退出游戏。" -ForegroundColor Red
    exit 1
}

Copy-Item $src $dst -Force

$hashSrc = (Get-FileHash $src -Algorithm SHA256).Hash
$hashDst = (Get-FileHash $dst -Algorithm SHA256).Hash
if ($hashSrc -ne $hashDst) {
    Write-Host "部署后哈希不一致，复制可能失败。" -ForegroundColor Red
    exit 1
}

Write-Host "已部署: $dst" -ForegroundColor Green
Write-Host ("SHA256: " + $hashSrc)
Write-Host "提示：拷贝成功不等于生效 —— 需重启游戏，并在 BepInEx 日志里确认 '失败 0'。" -ForegroundColor Yellow
