# check-targets.ps1 —— 用 Mono.Cecil 静态核验本插件的全部 Harmony 目标在游戏程序集里存在
#
# 为什么需要它：补丁目标写错（类型名/方法名/参数个数）时 Harmony 只会记一条日志，
# 功能静默失效，游戏内表现与"功能没生效"完全一样。上线前跑一遍比进游戏试快得多。
#
# 用法：
#   pwsh -File tools\check-targets.ps1
#   pwsh -File tools\check-targets.ps1 -GameDir "D:\...\Deadly Trick"

param(
    [string]$GameDir = $(if ($env:DEADLYTRICK_DIR) { $env:DEADLYTRICK_DIR } else { "C:\Program Files (x86)\Steam\steamapps\common\Deadly Trick" })
)

$ErrorActionPreference = "Stop"

$managed = Join-Path $GameDir "DeadlyTrick_Data\Managed"
$cecil = Join-Path $GameDir "BepInEx\core\Mono.Cecil.dll"
$asmPath = Join-Path $managed "Assembly-CSharp.dll"

foreach ($p in @($cecil, $asmPath)) {
    if (-not (Test-Path $p)) { Write-Host "找不到: $p" -ForegroundColor Red; exit 1 }
}

[void][System.Reflection.Assembly]::LoadFrom($cecil)
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
Write-Host ("程序集: " + $asm.Name.Name + " " + $asm.Name.Version) -ForegroundColor Cyan

# 拍平全部类型（含嵌套，Cecil 的嵌套分隔符是 '/'）
$all = @{}
$stack = New-Object System.Collections.Stack
$asm.MainModule.Types | ForEach-Object { $stack.Push($_) }
while ($stack.Count -gt 0) {
    $t = $stack.Pop()
    if (-not $all.ContainsKey($t.FullName)) { $all[$t.FullName] = $t }
    $t.NestedTypes | ForEach-Object { $stack.Push($_) }
}
Write-Host ("类型总数: " + $all.Count)

# 目标清单：类型全名 | 方法名 | 参数个数（-1 = 不校验参数）
$targets = @(
    @{ Type = "Server.Game.Device";           Method = "RecordLastUsingPlayer"; Args = 2; Note = "记录详细活动" }
    @{ Type = "Server.Game.Device";           Method = "Scan";                   Args = 1; Note = "扫描时补 what" }
    @{ Type = "Server.Game.GameRoom";         Method = "Broadcast";              Args = 2; Note = "广播时补 what" }
    @{ Type = "Server.Game.DeviceManager";    Method = "Scan";                   Args = 2; Note = "放行旁观者" }
    @{ Type = "Util";                         Method = "FormatProposition";      Args = 1; Note = "拼回 what" }
    @{ Type = "Util";                         Method = "TestFormatProposition";  Args = 1; Note = "调试句式" }
    @{ Type = "ClueManager";                  Method = "TakeDeviceClue";         Args = 1; Note = "线索↔设备关联" }
    @{ Type = "Server.Game.ClueMirror";       Method = "Add";                       Args = 2; Note = "广播线索自带设备号（精确路径）" }
    @{ Type = "Server.Game.TrialManager";    Method = "set_State";                 Args = 1; Note = "二次投票：阶段改道" }
    @{ Type = "Server.Game.TrialManager";    Method = "VotePlayer";                Args = 2; Note = "二次投票：拒收投黑幕的票" }
    @{ Type = "DeviceBase";                   Method = "Scan";                   Args = 0; Note = "记录待扫设备" }
    @{ Type = "Managers";                     Method = "Update";                 Args = 0; Note = "补扫调度" }
    @{ Type = "UI_LobbyPreset";               Method = "Init";                   Args = -1; Note = "设置页建行" }
    @{ Type = "UI_LobbyPreset";               Method = "Refresh";                Args = 0; Note = "设置页同步" }
    @{ Type = "UI_GameTablet";                Method = "Init";                   Args = -1; Note = "伪证下拉建控件" }
    @{ Type = "UI_GameTablet";                Method = "OnClickSectorLieSection"; Args = 2; Note = "伪证下拉刷新" }
    @{ Type = "Server.Game.HostPeerSession";  Method = "Send";                    Args = 1; Note = "能力声明时机（客户端出口）" }
    @{ Type = "DummyClient.SteamP2PSession";  Method = "Send";                    Args = 1; Note = "自定义协议改道（P2P 出口）" }
    @{ Type = "DummyClient.ServerSession";    Method = "Send";                   Args = 1; Note = "自定义协议改道（另一条出口）" }
    @{ Type = "Server.Game.DeviceManager";    Method = "StartFuseboxSabotage";   Args = 0; Note = "隐蔽破坏点：与电闸共用的 CD 信号" }
    @{ Type = "Server.Game.GameRoom";         Method = "SurvivalTick";           Args = 0; Note = "隐蔽破坏点：每秒维护假主人" }
    @{ Type = "Server.Game.ItemManager";      Method = "DropItem";               Args = 1; Note = "隐蔽破坏点：拦丢弃" }
    @{ Type = "Server.Game.ItemManager";      Method = "AcquireItem";            Args = 2; Note = "隐蔽破坏点：拦拾取" }
    @{ Type = "Server.Game.GameRoom";         Method = "ChangeGameState";        Args = 1; Note = "隐蔽破坏点：离开 Survive 收尾" }
)

# 被引用的成员（非补丁目标，但编译期引用；游戏升级后改名会直接编译失败，这里也顺手核一遍）
$members = @(
    @{ Type = "Protocol.PropositionInfo";     Member = "Clone";        Kind = "M"; Note = "未知字段载体" }
    @{ Type = "Server.Game.Player";           Member = "IsSpectator";  Kind = "P"; Note = "旁观者放行" }
    @{ Type = "Data.DeviceData";              Member = "WhatType";     Kind = "F"; Note = "活动权威来源" }
    @{ Type = "Data.DeviceData";              Member = "RoomType";     Kind = "F"; Note = "按房间推断" }
    @{ Type = "ClueManager";                  Member = "ClueList";     Kind = "F"; Note = "线索列表" }
    @{ Type = "UI_GameTablet";                Member = "LieClue";      Kind = "P"; Note = "伪证线索对象" }
    @{ Type = "DummyClient.SteamP2PSession";  Member = "SendBytes";    Kind = "M"; Note = "裸字节出口（public）" }
    @{ Type = "Server.Game.Device";           Member = "BroadcastState"; Kind = "M"; Note = "破坏点状态广播" }
    @{ Type = "Server.Game.DeviceManager";    Member = "SpawnDevice";    Kind = "M"; Note = "破坏点创建" }
    @{ Type = "Server.Game.DeviceManager";    Member = "Objects";        Kind = "P"; Note = "设备表（幂等判重）" }
    @{ Type = "Server.Game.Player";           Member = "Color";          Kind = "P"; Note = "黑幕判定（未破坏只发黑幕）" }
    @{ Type = "Server.Game.TimeManager";      Member = "SurviveTime";    Kind = "P"; Note = "进度与节流的时间基准" }
    @{ Type = "Protocol.DeviceInfo";          Member = "StateList";      Kind = "P"; Note = "0=正常 / 2=已破坏；[1]=假主人" }
    @{ Type = "Server.ObjectUtils";           Member = "GenerateNewId";  Kind = "M"; Note = "进度条汽水的 ObjectId" }
    @{ Type = "Server.Game.GameRoom";         Member = "BroadcastAlivePlayers"; Kind = "M"; Note = "断电箭头（只发存活者）" }
    @{ Type = "Server.Game.GameRoom";         Member = "SendSabotageMission";   Kind = "M"; Note = "电闸图钉（原版只发黑幕）" }
    @{ Type = "Server.Game.TimeManager";      Member = "PushSurvivalJob";       Kind = "M"; Note = "破坏 10 秒后再发箭头" }
)

$fail = 0

Write-Host "`n=== 补丁目标 ===" -ForegroundColor Cyan
foreach ($t in $targets) {
    if (-not $all.ContainsKey($t.Type)) {
        Write-Host ("  FAIL  {0}::{1} —— 类型不存在" -f $t.Type, $t.Method) -ForegroundColor Red
        $script:fail++
        continue
    }
    $type = $all[$t.Type]
    $cands = @($type.Methods | Where-Object { $_.Name -eq $t.Method })
    if ($cands.Count -eq 0) {
        Write-Host ("  FAIL  {0}::{1} —— 方法不存在" -f $t.Type, $t.Method) -ForegroundColor Red
        $script:fail++
        continue
    }
    if ($t.Args -ge 0) {
        $hit = @($cands | Where-Object { $_.Parameters.Count -eq $t.Args })
        if ($hit.Count -eq 0) {
            $sig = ($cands | ForEach-Object { "(" + (($_.Parameters | ForEach-Object { $_.ParameterType.Name }) -join ",") + ")" }) -join " "
            Write-Host ("  FAIL  {0}::{1} 参数个数不是 {2}（实际 {3}）" -f $t.Type, $t.Method, $t.Args, $sig) -ForegroundColor Red
            $script:fail++
            continue
        }
    }
    Write-Host ("  ok    {0}::{1}  {2}" -f $t.Type, $t.Method, $t.Note) -ForegroundColor Green
}

Write-Host "`n=== 引用成员（UnknownFieldSet 在 Google.Protobuf.dll，由启动自检负责） ===" -ForegroundColor Cyan
foreach ($m in $members) {
    if (-not $all.ContainsKey($m.Type)) {
        Write-Host ("  FAIL  {0}.{1} —— 类型不存在" -f $m.Type, $m.Member) -ForegroundColor Red
        $script:fail++
        continue
    }
    $type = $all[$m.Type]
    $found = switch ($m.Kind) {
        "F" { @($type.Fields | Where-Object { $_.Name -eq $m.Member }).Count -gt 0 }
        "P" { @($type.Properties | Where-Object { $_.Name -eq $m.Member }).Count -gt 0 }
        default { @($type.Methods | Where-Object { $_.Name -eq $m.Member }).Count -gt 0 }
    }
    if ($found) {
        Write-Host ("  ok    {0}.{1}  {2}" -f $m.Type, $m.Member, $m.Note) -ForegroundColor Green
    } else {
        Write-Host ("  FAIL  {0}.{1} —— 成员不存在" -f $m.Type, $m.Member) -ForegroundColor Red
        $script:fail++
    }
}

Write-Host ""
if ($fail -gt 0) {
    Write-Host ("check FAIL: " + $fail + " 项") -ForegroundColor Red
    exit 1
}
Write-Host "check PASS：全部补丁目标与引用成员存在" -ForegroundColor Green
