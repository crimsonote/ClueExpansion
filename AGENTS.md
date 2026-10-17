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

1. **不许污染原版字段。** 详细活动只能走 `PropositionInfo` 的 protobuf **未知字段（字段号 100）**；
   任何改写 `RoomId`/`PlayerId`/`Time` 的方案都会让**未装插件的客户端**显示错乱，绝对禁止。
   （原版对未知字段是"原样保留并忽略"—— 这正是这条通道成立的依据，见 §4.2。）
2. **未装插件的一端必须不炸。** 跨端数据分**两条通道**，按数据性质选，不是二选一（2026-10-18 定案）：
   - **线索的「详细活动」→ protobuf 未知字段**（`Core/UnknownFields.cs`，字段号 **100**）。
     它是**线索的一个属性**，附着在 `PropositionInfo` 上天然一一对应：零关联键、无时序要求、
     不会出现"同房间同时刻两条线索撞键"。
     ⚠️ **写入必须早于 `Clone()`** —— `Device.Scan` 改的是 `ClueList` 里的对象引用
     （随后 `AddRange` 进包），而 `RecordLastUsingPlayer` 是"先 `Clone()` 再 `Broadcast`"，
     所以那条路只能在 `GameRoom.Broadcast` 的 Prefix 里、对着包内的 `Info` 补。
     ⚠️ 字段号**刻意不用 4 和 6**：4 是官方刚弃用的号（可能回收），6 紧邻 `Destroyed=5`
     （"下一个新字段"最可能挑的）。100 落在明显没人用的租用区。
     代价：反射 `Google.Protobuf` 私有成员（`_unknownFields`/`fields`/`GetOrAddField`/`varintList`），
     换版本可能断 —— 启动自检会**真的写一次读一次**来验，断了会在日志里明说而不是静默失效。
   - **握手与开关状态 → 自定义 opcode**（`Core/Net/`，段 `61000` 起）。
     它们**没有宿主对象可附着**，用自定义协议才能自己选时机（进房即声明、改设置即广播）。
   ⚠️ **自定义 opcode 是可行的**（早前写成"完全不可行"是错的，同日更正）：受限的只有
     `IPacketSink.Send(IMessage)` —— 它用 `Enum.TryParse(PacketID, Descriptor.Name)` 反解 opcode
     （:163396 / :162553），自定义消息名不在枚举里只会打条 LogError 后 return。
     但裸字节通道是 public 的：客户端 `SteamP2PSession.SendBytes(byte[])`（:163423）、
     服务端 `Session.Send(ArraySegment<byte>)`（:162286，`ServerSession : PacketSession : Session`）；
     接收端 `PacketManager.RegisterMake<T>` / `RegisterHandler` 也是 public（:44876 / :44881）。
     包格式逐字节复刻原版：`int32 总长 | uint16 opcode | body`（:163403-163406）。
   - **不让原版坏的四条保证**：① 只拦 `packet is CdMessage`，原版消息一律走原路径、一个字节不改；
     ② opcode 取高位段；③ **注册前查 `_makeFunc` 占用，撞车就放弃注册并报错、绝不覆盖**
     （`RegisterMake` 是覆盖语义，覆盖掉原版注册等于直接弄坏那条包）；
     ④ 未装端 `_makeFunc` 查不到会静默丢弃（:44894），不报错、不踢人。
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
| `Protocol.PropositionInfo`（原版字段 1/2/3/5） | 157635 | 未知字段载体；本模块用字段 **100** 放详细活动 |
| `Data.DeviceData.WhatType` | 182965 | 活动的权威来源 |
| `Managers.Data.TextDic`（public Dictionary） | 34272 | 词条注入 |
| `Managers.GetText(string)` | 43295 | 命中不到会返回 key 本身并报错 |
| `Managers.Language` | 43200 | 当前语言（`Define.ELanguage`） |
| `Data.DeviceData.RoomType` / `.WhatType` | 182965 / 182979 | 按房间推断活动（客户端静态表） |
| `Server.Game.DeviceManager.Scan(Player,int)` | 166680 | 旁观者守卫（放行挂点） |
| `Server.Game.Player.IsSpectator` | 29694 | 可写属性（放行时临时置 false） |
| `Managers.Update` | —— | 补扫调度（每帧一次状态检查） |
| `Managers.Device.Cache` | —— | 客户端已加载设备（`Info.Bubble == -1` = 可调查） |
| `DummyClient.SteamP2PSession.Send` / `.SendBytes` | 163379 / 163423 | 自定义协议改道 + 裸字节出口（public） |
| `DummyClient.ServerSession.Send` | 162551 | 同上（另一条出站路径） |
| `PacketManager.RegisterMake` / `RegisterHandler` | 44876 / 44881 | 注册自定义 opcode（public） |

`Core/LocalizationData.cs` 是**生成文件**（46 条词条 × 5 语言），
来源 `.tmps/gamedata/TextData.json`（0.1.16b 基线），不要手改。

## 6. 待办

按依赖顺序：

1. ~~客户端能力握手~~ **已做**：分两条 ——
   ① **能力声明**：`Features/Both/CdProtocol/` 在客户端第一次发包时发一条 `CdHello`（自定义 opcode），
   服务端收到即登记（`Core/ClientCapability.cs`）；
   ② **房主开关同步**：服务端经 `CdState` 下发，客户端在 `RoomState` 里跟随。
   两者都走自定义协议而不是"寄生在原版包的未知字段上"—— 时机自由，
   不必再为了赶 `Player` 绑定的窗口而冗余到四五个原版包上。
2. ~~房主开关 + 同步~~ **已做**：`[ClueRecordDetail] BroadcastDetail`，值经 `CdState` 下发
   （改设置立刻广播，不必等下一次线索包），客户端在 `RoomState` 里跟随
   （房主关闭 ⇒ 客户端连自采集一起停）。
3. ~~大厅设置页单选框~~ **已做（待实机）**：`Features/UI/LobbyToggle/` 克隆原版开关行挂进
   「详细设置」的「其它」页；仅房主可改；只有装了本模块的玩家能看到这一行。
   ⚠️ 0.1.18 动过 `common_prefab`，若日志出现「找不到设置页内容容器 ETCPresetContent」，
   说明 prefab 层级又变了，按 HideAndSeek 的 `Features/UI/EXTENDING.md` §6 重新对名字。
4. ~~伪证 UI 还原~~ **已做（待实机）**：`Features/Client/LieWhat/` 克隆界面上已有的下拉作为
   控件，选项用 `RoomDeviceScan`（旧版是已删除的 `ClueManager.RoomObjectDict`），
   选中后写进 `LieClue.Clue.Info` 的未知字段。提交链路不用改：服务端 `SubmitProposition`
   原样保存并广播 `Info`（0.1.18 :182232-182246），未知字段自动传遍全房。
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

## 8. 与 DT_Tools 的集成（可选依赖）

**不装 DT_Tools 也能跑**：集成全程走反射（`AccessTools.TypeByName("DT_Tools.Core.Engine")`），
csproj **不引用**它的程序集，拿不到就只记一行提示。

**已做 · 配置镜像**（`Core/Dt/DtBridge.cs` + `Core/Dt/ConfigEntries.cs`）：
把本模块的条目注入 DT 的 `ConfigFile`，使设置出现在 DT 的 CONFIG 页。
机制是反射 BepInEx 的 `<Entries>k__BackingField` 拿到条目字典，再把**同一批 `ConfigEntry` 对象**
塞进去 —— 值只有一份，两边读写的是同一个值，不需要任何同步代码。
- ⚠️ 代价：DT_Tools 的 `Save()` 会把这些条目一并写进**它自己的** cfg（镜像的固有代价，与 HideAndSeek 相同）。
- ⚠️ 逐键查重：撞名就跳过并告警。覆盖上游条目远比"少显示几项"严重。
- ⚠️ 必须在**全部 Bind 之后**调用，否则会镜像进一批残缺条目。

**实测口径：游戏里的 DT_Tools 是 `2.0.0.4`**（2026-10-18 核对 —— 与 HideAndSeek 文档里记的 1.0.9.x 不同）：

| 集成点 | 2.0 状态 |
| --- | --- |
| 配置入口 `DT_Tools.Core.Engine.Config` | ✅ 仍在（public static 属性） |
| 命令桥-执行 `WebConsole.ExecuteCommandText(string)` | ⚠️ **已降为 `private static`**（类名与签名都没变）⇒ 仍可行，挂载改用字符串名（HideAndSeek 就是这么做的） |
| 命令桥-列表 `CommandsApi._cachedJson` | ✅ **仍在**，类移到了 `DT_Tools.WebConsole.Api.CommandsApi` |
| `SectionCategories` | ❌ 已删除；替代品是 `SectionGroups`（段名→组）+ `SectionTags` + `SectionSides` |

所以：**没有做分类登记**（旧字段已删，替代品 `SectionGroups` 尚未复核）；
**命令桥暂不做** —— 注意**不是**因为上游不可用（执行口与列表缓存都还在，见上表），
纯粹是本模块目前没有命令域。将来要加的话，只差两步：
`AccessTools.Method(type, "ExecuteCommandText", …)` 做执行口 + 反射填 `CommandsApi._cachedJson` 做列表。

⚠️ **核对这类"上游接口变没变"时**（2026-10-18 我连错两次，记下来）：
① 查类型**别用全名精确匹配** —— 2.0 把 API 层整体挪进了 `DT_Tools.WebConsole.Api.*`，
精确查会得到"不存在"的假阴性，要用**后缀匹配**；
② 查成员**别过滤可见性** —— `Where-Object { $_.IsPublic }` 会把降级成 `private` 的成员藏起来；
③ **终极依据是调用方代码里的字符串常量**（HideAndSeek 的 `ConsoleBridge.cs` 顶部那 7 个
`Dt*TypeName`），DLL 扫描说"不在"时先核对常量写的是什么。详见 HideAndSeek/AGENTS.md §6 末节。