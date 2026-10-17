# ClueDetail 开发规范

《Deadly Trick》的**独立** BepInEx 5 插件，用于还原 0.1.18 从线索里移除的「详细活动」。

**核心约束：零改动别的仓库。** 不修改 `DT_Tools\`、`HideAndSeek\` 下任何文件。

游戏版本基准：**0.1.18**（`Assembly-CSharp.dll`，反编译口径见 §5）。

---

## 1. 编码（最容易踩，且不报错）

**所有 `.cs` 与 `.ps1` 都必须带 UTF-8 BOM。**

中文 Windows 上 Roslyn 对"无 BOM + 非 ASCII"的 `.cs` 会退回**系统 ANSI（GBK）**读取，
中文字面量在编译期就已损坏且**没有任何警告**。`edit` / `write` 这类工具会丢 BOM，
**每次改完都要补**：

```powershell
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
Get-ChildItem -Recurse -Include *.cs,*.ps1 | Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' } | ForEach-Object {
    $b = [System.IO.File]::ReadAllBytes($_.FullName)
    if (-not ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)) {
        [System.IO.File]::WriteAllText($_.FullName, [System.Text.Encoding]::UTF8.GetString($b), $utf8Bom)
    }
}
```

`verify.ps1` 第 1、2 项就是 BOM 与乱码哨兵。

**验证产物而不是源码**：用 BepInEx 自带的 `Mono.Cecil` 读 IL 字符串操作数
（`#US` 堆是 UTF-16，`#Blob` 里的特性参数是 UTF-8，**整文件按固定宽度解码一定会错位**）：

```powershell
$dll = "bin\Release\netstandard2.1\ClueDetail.dll"
[void][System.Reflection.Assembly]::LoadFrom("$GameDir\BepInEx\core\Mono.Cecil.dll")
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dll)
# 遍历 Types/Methods/Instructions，收集 Operand -is [string]
```

`[PatchFeature(...)]` 的描述在 `#Blob`，`ldstr` 的文案在 `#US` —— 后者能搜到、前者搜不到是正常的。

## 2. 构建与部署

```powershell
dotnet build -c Release --nologo     # 必须 0 警告 0 错误
pwsh -File verify.ps1                # 必须 PASS
pwsh -File deploy.ps1                # 拷贝成功 ≠ 生效，必须重启游戏
```

- 必须在 `ClueDetail\` 目录内构建（根目录的 `global.json` 钉的是本机没有的 SDK）。
- 本机 `pwsh` 实为 Windows PowerShell 5.1；`dotnet` 为 8.x。
- 程序集直接引用**游戏安装目录**的 `DeadlyTrick_Data\Managed`（运行期加载的就是这一份）；
  换机器用 `-p:GameDir=...`。
- 不用 NuGet：沙箱内 HTTPS 到 nuget.org 不可用。

## 3. 代码组织

- 命名空间与目录一致。
- 一个功能目录 = 一个功能：`Feature.cs`（`[PatchFeature]` 声明）+ `Patch.cs`（补丁）。
  两者是**同一个 partial 类**，Harmony 的 `PatchAll` 能扫到另一个文件里的嵌套补丁类。
- `FeatureLoader` 自动：绑段开关 `[Section] Enabled` → 调用可选的 `static void Bind(ConfigFile, string)`
  → 启用则 `PatchAll`。**功能代码里禁止手写 `Config.Bind`。**
- ⚠️ **补丁嵌套只扫一层**：补丁类必须是 Feature 的**直接**嵌套类型，不能套两层。
- `Plugin.cs` 只组装，不含玩法逻辑。

## 4. 铁律（违反会静默失效或弄炸别人）

1. **不许污染原版字段。** 详细活动只能走 `PropositionInfo` 的 protobuf **未知字段（字段号 4）**
   —— 0.1.18 已弃用该号且未复用；任何改写 `RoomId`/`PlayerId`/`Time` 的方案都会让
   **未装插件的客户端**显示错乱，绝对禁止。
2. **未装插件的客户端必须保持原版行为。** 只用 protobuf 未知字段，不改原版包结构。
   ⚠️ **自定义 opcode 走不通（已实测证伪）**：`ServerSession.Send` 用
   `Enum.Parse(PacketID, packet.Descriptor.Name)` 反解 opcode（0.1.18 :162551），
   自定义消息的名字不在 `PacketID` 枚举里会直接抛异常；`PacketManager.RegisterMake`
   只能注册**接收**。所以跨端数据一律走 `PropositionInfo` 未知字段。
3. **同名类型必须显式限定**：本程序集有 `ClueDetail.Features.Client` / `...Host`，
   而游戏里 `Server.Game.*` / `Data.*` 到处重名。用别名：
   `using ServerDevice = Server.Game.Device;`、`using GamePlayer = Server.Game.Player;`。
   已确认的命名空间：`GameRoom`/`TimeManager`/`Device`/`Player` 在 `Server.Game`；
   `TextData`/`DeviceData`/`RoomData` 在 `Data`；`EWhatType`/`ERoomType`/`PropositionInfo` 在 `Protocol`。
4. **Harmony 重载必须写参数类型**，私有方法用字符串名：
   `[HarmonyPatch(typeof(ServerGameRoom), "Broadcast", new[] { typeof(IMessage), typeof(int) })]`。
5. **内部类型**（如 `PacketHandler`）不能用 `typeof`，需 `AccessTools.TypeByName` +
   `[HarmonyTargetMethod]`。优先改挂 public 方法（例：挂 `ClueManager.TakeDeviceClue` 而不是
   `PacketHandler.Handle_S_SCAN_DEVICE`）。
6. **未知字段写入必须幂等**：`UnknownField.varintList` 是**追加**语义，重复写会变成两个值。
   `UnknownFields.SetWhat` 已内置 `GetWhat != 0` 短路。
7. **`Log` 会撞名**：游戏在**全局命名空间**有 `public static class Log`。本模块的日志门面在
   `ClueDetail.Core` 下 —— 跨命名空间（如 `ClueDetail.Features.*`）时必须写
   `global::ClueDetail.Core.Log.XXX`；`using Log = ...` 别名会被编译器直接拒绝
   （CS0576：别名不能与全局类型同名），实测踩过。
8. **克隆原版控件必须 `RemoveAllListeners()`**：`Instantiate` 会把原版监听器一起复制，
   症状是「拨我的开关连带改了原版设置」。写配置后必须显式 `Save()`，否则改完退出就丢。

## 5. 游戏 API 锚点（0.1.18）

反编译口径：`.tmps\decomp\Assembly-CSharp-0.1.18.decompiled.cs`（183351 行，2026-10-16）。
引用一律带行号，游戏升级后按这些行号逐条复核。

| 目标 | 行号 | 用途 |
| --- | --- | --- |
| `Server.Game.Device.RecordLastUsingPlayer(Player, EWhatState)` | 166386 | 记录 what（Prefix） |
| `Server.Game.Device.Scan(Player)` | 166315 | 扫描下发（Prefix 补 ClueList） |
| `Server.Game.GameRoom.Broadcast(IMessage, int)` | 174880 | 广播入口（Prefix 补 S_ADD_CLUE.Info） |
| `Util.FormatProposition(ClueManager.Clue)` | 27343 | 线索句式渲染（Postfix 拼回 what） |
| `Util.GetRoleWhatKey(int)` | 27517 | 0.1.18 的降级实现（只认角色 id） |
| `ClueManager.TakeDeviceClue(S_SCAN_DEVICE)` | 27588 | 客户端收扫描结果（Postfix 关联设备） |
| `DeviceBase.Scan()`（private） | 4952 | 客户端发起扫描（Prefix 记设备） |
| `Protocol.PropositionInfo`（字段 1/2/3/5） | 157635 | 未知字段载体 |
| `Data.DeviceData.WhatType` | 182965 | 活动的权威来源 |
| `Managers.Data.TextDic`（public Dictionary） | 34272 | 词条注入 |
| `Managers.GetText(string)` | 43295 | 命中不到会返回 key 本身并报错 |
| `Managers.Language` | 43200 | 当前语言（`Define.ELanguage`） |
| `Data.DeviceData.RoomType` / `.WhatType` | 182965 / 182979 | 按房间推断活动（客户端静态表） |
| `Server.Game.DeviceManager.Scan(Player,int)` | 166680 | 旁观者守卫（放行挂点） |
| `Server.Game.Player.IsSpectator` | 29694 | 可写属性（放行时临时置 false） |
| `Managers.Update` | —— | 补扫调度（每帧一次状态检查） |
| `Managers.Device.Cache` | —— | 客户端已加载设备（`Info.Bubble == -1` = 可调查） |

`Core/LocalizationData.cs` 是**生成文件**（46 条词条 × 5 语言），
来源 `.tmps/gamedata/TextData.json`（0.1.16b 基线），不要手改。

## 6. 待办

按依赖顺序：

1. ~~客户端能力握手~~ **已做（状态通道）**：房主侧把开关写进 `PropositionInfo` 未知字段（号 6，
   0 也写），客户端读取后跟随；房主没装时无标记 → 客户端自采集兜底。见 `Core/RoomState.cs`。
   剩下的**客户端能力上报**（服务端想知道谁装了）需要挂 internal 的 `HostPacketHandler`，
   用 `AccessTools.TypeByName` + `[HarmonyTargetMethod]`；目前不阻塞任何玩法。
2. ~~房主开关 + 同步~~ **已做**：`[ClueRecordDetail] BroadcastDetail`，值写进未知字段号 6，
   客户端在 `RoomState` 里跟随（房主关闭 ⇒ 客户端连自采集一起停）。
3. ~~大厅设置页单选框~~ **已做（待实机）**：`Features/UI/LobbyToggle/` 克隆原版开关行挂进
   「详细设置」的「其它」页；仅房主可改；只有装了本模块的玩家能看到这一行。
   ⚠️ 0.1.18 动过 `common_prefab`，若日志出现「找不到设置页内容容器 ETCPresetContent」，
   说明 prefab 层级又变了，按 HideAndSeek 的 `Features/UI/EXTENDING.md` §6 重新对名字。
4. **伪证 UI 还原**：0.1.18 的 `UI_GameTablet` 只剩 who/hour/minute 三个下拉
   （反编译 :51623-51631），旧版还有 `what` 下拉（0.1.17a 的 `RefreshWhat` /
   `OnWhatDropdownValueChanged` / `HasLieWhatOptions`）—— 需要按旧版重建一个 `TMP_Dropdown`。
5. ~~死者 / 旁观者全线索~~ **已做（待实机）**：`Features/Client/FullClue` 在调查阶段对本机
   `Bubble == -1` 的设备逐个补发扫描（每帧一个）；`Features/Host/SpectatorScan` 为旁观者
   放行服务端的 `IsSpectator` 守卫。房主没装时旁观者的请求没有响应，靠
   `ActivityMap.MatchesPendingRoom` 的房间校验挡住队列错位。
6. ~~自采集增强~~ **已做（换了更稳的手段）**：不再靠"记录自己操作过哪台设备"——
   客户端时钟与服务端线索时间对不齐，而且同一线索可能对应多台设备。改为
   `Core/RoomDeviceScan` 按线索房间从设备表反推：去重后唯一 ⇒ 精确；
   多种 ⇒ 歧义，退化为通用文案「操作了设备」（`Localization.GenericActivityKey`）。

## 7. 过程材料

调查记录、一次性脚本、被证伪的方案放 `D:\git\DT_Tools\.tmps\`（**不进任何仓库**）；
耐久结论写进本文件或 `README.md`。
