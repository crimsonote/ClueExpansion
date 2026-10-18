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
  两者是**同一个 partial 类**；补丁类是它的**嵌套类**，由装载器**逐个 `PatchAll(补丁类)`** 挂载。
  ⚠️ 见 §11.7 —— 用 `harmony.PatchAll(功能类)` 是**一个补丁都挂不上**的。
- `FeatureLoader` 自动：绑段开关 `[Section] Enabled` → 调用可选的 `static void Bind(ConfigFile, string)`
  → 启用则 `PatchAll`。**功能代码里禁止手写 `Config.Bind`。**
- **开关全是热的**（2026-10-18 起：改 `.cfg` 不用重启游戏）——见 §11。三条配套约定：
  ① 每个补丁的**第一行**必须写 `if (!FeatureLoader.Enabled<XxxFeature>()) return;`
  （返回 bool 的 Prefix 写 `return true;` —— 放行原方法）；
  ② 有状态副作用的必须实现 `OnDisabled` 收拾干净；
  ③ **功能类写 `internal partial class`，不能加 `static`** —— 它要给 `Enabled<T>()` 当类型参数（见 §11.4）。
- ⚠️ **补丁嵌套只扫一层**：补丁类必须是 Feature 的**直接**嵌套类型，不能套两层。
- `Plugin.cs` 只组装，不含玩法逻辑。

## 4. 铁律（违反会静默失效或弄炸别人）

1. **不许污染原版字段。** 详细活动只能走 `PropositionInfo` 的 protobuf **未知字段（字段号 100）**；
   任何改写 `RoomId`/`PlayerId`/`Time` 的方案都会让**未装插件的客户端**显示错乱，绝对禁止。
   （原版对未知字段是"原样保留并忽略"—— 这正是这条通道成立的依据，见 §4.2。）
   **例外备案（2026-10-18）**：`Features/Host/ShadowSabotage/` 会**故意**改写线索包里的
   `Time`，并把 `EWhatState` 从 `Normal` 改成 `Anonymity`（⇒ `PlayerId` 变 0）。
   那不是搭便车传数据，而是功能本身要的**效果** —— 必须让**未装本模块**的客户端
   也看到「某人 / 冻结的时间」，否则"隐蔽破坏"就白做了。
   边界：只在 Survive 阶段、只在对应破坏点处于已破坏状态时生效；只改**包内 / 克隆**的副本，
   绝不动服务端 `ClueList`（依据见 §10.1 末行与 §10.2 第 4 条）。
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

**基准指纹**（2026-10-18 记录，用来判断"游戏是否已升级"）：

| 项 | 值 |
| --- | --- |
| `Assembly-CSharp.dll` 文件时间 | 2026-10-16 16:37:19 |
| MVID | `baf64ef4-e6fb-4b89-b45c-3913dcfd481a` |

⚠️ 判断基准是否还成立，**最快的办法是重跑 `tools/check-targets.ps1`** ——
它直接读游戏 DLL 逐个验证补丁目标存在，PASS 就说明行号锚点仍然有效（比对比 MVID 更直接）。

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
| `UI_ClueSubItem.FillText(ClueManager.Clue)`（private） | 86635 | 线索卡片补「活动卡面 / 整句」的挂点 |
| `UI_ClueSubItem.ClearInfo()`（private） | 86739 | 无效线索：清掉我们补的那一格 |
| `UI_ClueSubItem.OnEnterClue` / `.OnExitClue(PointerEventData)`（private） | 86535 / 86545 | 悬浮切「图标视图 / 整句视图」 |
| `ResourceManager.Load<T>(string)` | 36766 | 按地址取精灵（活动卡面）——**预载命中才拿得到** |
| `Managers.Data.RoomDic` / `Data.RoomData.Type` | 183077 | 房间类型 → 卡面资源名 |
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
6. ~~自采集增强~~ **已做（2026-10-18 修正主路径）**——三级，可靠性递减：
   - **主路径 · 广播自带设备号**：`S_ADD_CLUE` 的字段是 `DeviceId = 1` + `Info = 2`（:145153），
     客户端收到后进 `Server.Game.ClueMirror.Add(deviceId, info)`（:43626 / :180795）——
     在它的 Prefix 里记下 `(playerId,time,roomId) → deviceId`，渲染时直接查设备表的
     `WhatType`。**精确，且覆盖所有实时广播的线索**（不论谁触发、自己有没有扫过）。
     ⚠️ 早前只在扫描那条路上做关联，把包里现成的设备号浪费了，才退到"按房间猜"。
   - **次路径 · 扫描配对**：`S_SCAN_DEVICE` 只带 `InfoList`、**没有**设备号（:127846），
     但那是本机刚发起的扫描，用 `ActivityMap` 的队列 + 房间号校验配对。
   - **兜底 · 按房间推断**：`Core/RoomDeviceScan` 从线索房间反推设备表里有哪些活动。
     去重后唯一才敢用，多种就退化为通用文案「操作了设备」（`Localization.GenericActivityKey`）。
     只有在上面两条都没命中时才会走到这里。

7. ~~隐蔽破坏点~~ **已做（待实机）**：`Features/Host/ShadowSabotage/` —— 两个伪装成小熊的
   服务端破坏点（科学室的身份识别组件 / 电脑室的时钟记录组件），与电闸共用同一条 60 秒 CD。
   含身份线索污染、时间冻结、断电箭头、黑幕专属电闸图钉、破坏点自身可被调查。**详见 §10。**

8. ~~线索卡片回退旧版 UI~~ **已做（待实机）**：`Features/Client/ClueCard/`。
   0.1.18 重做了 `UI_ClueSubItem` prefab（见 §12），把旧版的两格删掉了 ——
   「活动卡面（设备图像）」与「整句文本」，只剩「房间卡 + 房间名 + 分隔线 + 时间」。
   本功能在运行期补回那两格：活动卡面克隆 `ClueWhereImage`、整句克隆 `ClueWhereText`，
   悬浮（`OnEnterClue` / `OnExitClue`）时藏起图标组、把整句显示出来。
   版式（各栏位置与宽度）**全部是配置项**（段 `[ClueCard]`），改 `.cfg` 就能调，不必重编。
   ⚠️ 伪证行（`Statement*`）也一并支持，但它的版式是**按空档自动排**的（没有配置项）。

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

## 9. 审判规则：黑幕二次投票（`Features/Host/SecondVote/`）

第一次投票若**投出黑幕**，不作终局结算，而是把黑幕标为本轮不可投并**立即重开一轮投票**（不重开讨论）。
其余情况一律与原版一致。

**三条机制依据（0.1.18，都能直接复核）**：

| 用到的机制 | 位置 | 说明 |
| --- | --- | --- |
| 投票界面"谁能被投"完全由服务端决定 | `S_SEND_TRIAL_EVENT.PlayerInfos`（`repeated TrialInfo{PlayerId,IsAlive}`）→ 客户端 `UI_TrialEvent.SetInfo`（:71934）→ `UI_TrialPortraitSubItem.SetInfo(_, info.IsAlive, …)`（:71981） | 点选时校验的就是这个 `IsAlive`（:73774 的 `IsReadyVote`） |
| 阶段切换由服务端一处收口 | `TrialManager.State` 的 setter（:181888）内部就是 `ChangeTrialState`（:182269，广播 `S_TRIAL_STATE` + `StartState`） | 补丁改 `value` = 把这一跳改成另一跳 |
| 重开一轮不会卡"已投票" | 客户端 `StartVotePhase()` 首先 `_hasVoted = false`（:73054） | 所以重复进入 `VotePhase` 是安全的 |

**实现要点**：

- 改道：`set_State` 的 Prefix 里，当目标是 `Replay`/`TrialResult` 且 `MostVotedPlayerId()`（:182607）
  等于黑幕 id 时，把 `value` 改成 `VotePhase`；`Discuss` 时重置本局标记（第二轮不经过 Discuss）。
- 不可投：**自己构造并发一条 `S_SEND_TRIAL_EVENT`**（黑幕的 `IsAlive` 报 false）。
  ⚠️ **绝不改 `Player.IsAlive`** —— 那只影响客户端投票界面，游戏逻辑与结算不受影响。
- 兜底：`VotePlayer`（:182443）Prefix 拒收投给黑幕的票 —— 未装本模块的客户端仍点得到他，
  没有这一层"不可投"就只是视觉效果。
- 票箱：原版只在 `TrialManager.Init`（:182106）重建 `_candidates`，第二轮沿用同一张列表，
  所以必须手动把 `Info.VoteCount` / `Vote` / `VotedTargetId` 清零，否则两轮票数叠加。

⚠️ **已接受并记录的副作用**：客户端收到 `IsAlive=false` 会 `KnownDeadIds.Add`（:71977），
连带把黑幕在**本轮审判**里当死者 —— 语音静音（:70861）、聊天进死者频道（:42820）、
绘画面板列为死者（:54629）。该列表每次审判 `SetInfo` 会先清空（:71945），影响止于审判阶段；
原版处刑走的是同一套（`MarkExecutedAsDead` :72629）。

## 10. 隐蔽破坏点（`Features/Host/ShadowSabotage/`，纯服务端）

两个**伪装成小熊**的服务端破坏点，与电闸共用同一条 60 秒 CD。

| 点 | 默认坐标 | 破坏期间的效果 |
| --- | --- | --- |
| 身份识别组件 | `(6608, 4700)` 科学室最左 | 新线索的 `PlayerId` 一律变 0 ⇒ 全场渲染「某人」（`WhoSomeone`） |
| 时钟记录组件 | `(9561, 3430)` 电脑室刀架下 | 新线索的 `Time` 冻结在破坏那一刻（**不影响**报告里的死亡时间） |

- **未破坏时只有黑幕**（`EPlayerColor.Dark`）能按 E 破坏；**破坏后所有人**可修。
  两者都用原版摇汽水条（物品 1039，满值 24，100ms 自续链），**打断保留进度**。
- 破坏 **+10 秒**给**所有存活者**发断电箭头（`FuseboxArrow`）；**刻意不发任何文字通知**。
- 地图上的电闸图钉**只给黑幕**（走原版 `SendSabotageMission`，它本就只发 `MasterMind`）。
- 破坏点自己留到调查阶段，**任何人**都能扫出「黑幕 于 … 在 科学室 切断了电力」。
- **CD 中**：无图钉 + 不可交互；但**小熊本体始终在场**（留到调查阶段用）。
- 文件：`Feature.cs`（配置）/ `Patch.cs`（补丁）/ `Logic.cs`（编排 + 污染判定）/
  `State.cs`（运行期状态）/ `Device.cs`（`BreakerDevice`）/ `Bar.cs`（进度条）/ `Wire.cs`（包构造）。

### 10.1 机制依据（0.1.18，动手改之前逐条复核）

| 用到的机制 | 位置 | 说明 |
| --- | --- | --- |
| 客户端按什么选 prefab | `Managers.Device.HandleAddDevice` :28247 | 先查 `DeviceDic[DeviceId]`，**没命中才退回 `info.Type.ToString()`** ⇒ 只要 ID 不在那张表里就用 `"Summon"`（小熊） |
| 小熊贴图/状态刷新 | 客户端 `Summon.RefreshState` :4136 | **断言 `StateList.Count == 2`** ⇒ 第三槽会让整段刷新被跳过 |
| 按 E 提示从哪来 | 客户端 `Summon.GetInteractMessage` :4159 | 只认 `StateList[1] == MyPlayerId` + `_isActivate`（出生动画约 1.5s 后才 true） |
| 按 E 的服务端链路 | `Handle_C_INTERACT_SUMMON` :177619 → `DeviceManager.Interact` :166617 → `Device.Interact` | **不校验主人**，只看设备 ID ⇒ 假主人是纯客户端观感 |
| 交互的阶段守卫 | `DeviceManager.Interact` :166617 | 非 Door/Cabinet/Vent 的设备**只在 `State == Survive` 且未 AllClear** 时转发 |
| ⚠ 可调查与按 E **互斥** | 客户端 `DeviceBase.InteractBase` :4843 / `GetInteractMessageBase` :4880 | `Bubble == -1` 是**第一优先** ⇒ 置上之后按 E 只会「调查」，**再也不能修复** |
| 可调查自动生效 | `Device.StartDetective` :166531 | `ClueList.Count == 0 ? Bubble = 0 : Bubble = -1` 并广播 ⇒ **只要塞了线索就不用自己设 Bubble** |
| 与电闸共用 CD | `DeviceManager.StartFuseboxSabotage` :166828 | 调用点只有 `InitDevices`（开局 :167336）与 `RefreshLight` 的 case 0（供电全恢复 +60s :176585） |
| 电箱图钉只发黑幕 | `GameRoom.SendSabotageMission` :174389 | 只发给 `MasterMind`（且要求 `IsAlive`） |
| 箭头写法 | `DeviceManager.BroadcastFuseArrow` :166870 | `BroadcastAlivePlayers(S_NOTIFY_ARROW{FuseboxArrow, Pos})` |
| 身份污染的值 | `Device.RecordLastUsingPlayer` :166393 | `Anonymity` ⇒ `playerId = 0`；`Mastermind` ⇒ 11037；`Black` ⇒ 66613 |
| 时间污染为什么安全 | 同上 :166415-166436 | 原版**先 `Clone()` 再 `Broadcast`** ⇒ 改包内 `Info` 不碰服务端 `ClueList` 与 30 秒去重窗口 |

### 10.2 六个必须记住的坑（都实际踩过或有硬依据）

1. **设备 ID 取 90 万段**（`900101` / `900102`）。客户端按 `DeviceDic` 是否命中决定 prefab，
   ID 命中就会变成别的设备外观；而 `ObjectUtils.GenerateNewId()`（原版小熊用的那个）
   从 16 起递增、**每局 `Clear()` 还会重置**，我们的设备跨局常驻，用它迟早撞车。
2. **`StateList` 必须恰好 2 槽**：`[0]` = 0 正常 / **2 已破坏**（避开 `1` —— `1` 会触发
   范围圈 `ShowRange` 与麦克风静音 `MuteMicWorld`），`[1]` = 假主人位。
   进度、CD、伪装名单一律放服务端字典。
3. **不走 `DeviceManager.CreateSummon`**：一旦进 `_summons` 就会被凛的附身技能
   （`PossessMarionette` → `GetSummon`）认领。我们直接 `SpawnDevice`。
4. **两个污染钩子的 `[HarmonyPriority]` 不能省**（与本模块 `RecordDetail` 抢同一个方法）：
   - 身份：`Priority.First` —— `RecordDetail.RecordHook` 按 `state` 算 `playerId` 记旁路表，
     必须让它看到改过之后的值，两边键才对得上；
   - 时间：`Priority.Last` —— 分别要**晚于** `BroadcastHook`（它拿 `(PlayerId, Time, RoomId)`
     查表，改早了就永远查不到）与 `ScanHook`（它往 `ClueList` 的对象上补「详细活动」，
     我们克隆得早了线索就没详细活动）。
5. **必须拦 `ItemManager.DropItem` / `AcquireItem`**：客户端 `DropHand` 是**先本地清手再发包**，
   放行的话他会把**真物品**丢地上、进度条同时消失（两个钩子只在 `BreakBar.IsActive` 时生效）。
6. **广播会冲掉假主人**：`BroadcastState()` 带出去的是权威 `DeviceInfo`（其中 `StateList[1] == 0`），
   所以每次广播之后必须让伪装名单失效并立刻重发（`Finish` / `OnWindowStart` 都做了）。

### 10.3 配置（段 `[ClueShadowSabotage]`）

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `PosIdentifyX/Y` | 6608 / 4700 | 身份识别组件坐标 |
| `PosClockX/Y` | 9561 / 3430 | 时钟记录组件坐标 |
| `BreakSeconds` | 5 | 进度条推满所需秒数（破坏与修复共用） |
| `InteractRadius` | 120 | 假主人**进入**半径（原版小熊交互框是 ±75，所以 120 更宽松） |

**刻意不给配置项的三个**（都是实现细节，写死更不容易被调歪）：离开半径 = 进入半径 × 4/3（滞回）、
范围内重发间隔 = 1.5 秒、破坏后发箭头的延迟 = 10 秒。

### 10.4 未实机验证（交给使用者在游戏里过一遍）

1. 两个点的坐标是否落在预期位置（科学室最左 / 电脑室刀架下）；
2. 假主人的生效半径与「按 E」提示是否如期出现（**注意**：假主人会带来原版小熊的
   近身检测雷达 —— `RuneSfx` + HUD 描边，判据是 `IsOwner`，**纯服务端规避不掉**，已接受）；
3. 进度条观感与打断后的续跑；
4. 与电闸那 60 秒的联动（电闸重派发时两个点是否恢复可破坏、图钉是否回来）；
5. 时间污染期间客户端线索按 `Time` 排序会不稳定（同批线索时间相同，原版 `Clue.CompareTo`
   用的是 `Time`）—— 已知且可接受。

## 11. 开关体系（2026-10-18 起：全部热更新，空转式）

三个层次，**改完立刻生效，不用重启游戏**：

| 层次 | 键 | 粒度 |
| --- | --- | --- |
| 总开关 | `[ClueDetail] Enabled` | 一次停用**全部**功能 |
| 段开关 | `[<段名>] Enabled` | 单个功能 |
| 子配置 | 各功能自己的键 | 每次读 `ConfigEntry.Value` |

### 11.1 空转式（与 DT_Tools 的 `Engine.Enabled<T>()` 同款）

- **补丁一律挂着**：`FeatureLoader.Load` 对所有功能**无条件 `PatchAll`**，
  开关只决定**每个补丁入口那行检查**放不放行；
- `FeatureLoader.Enabled<T>()` **每次都读 `ConfigEntry.Value`** —— 这就是「热」的全部来源，
  切换时**不碰任何 IL**；
- ⚠️ 曾用「卸载式」（每功能一个 Harmony 实例 + `UnpatchSelf()`）实现过同一效果，
  **已放弃**：功能等价，但要多一套机制，而 DT_Tools 用空转式早就跑通了同样的语义
  （它的 `Mount` 也是无条件 `PatchAll`，`EnabledOf` 也是每次读值）。
- 代价：**入口检查必须写全** —— 漏一处就是「关了还生效」。

### 11.2 入口检查（硬要求，本项目共 28 处）

```csharp
private static void Postfix(...)
{
    if (!FeatureLoader.Enabled<RecordDetailFeature>()) return;
    ...
}

private static bool Prefix(...)        // ⚠️ 返回 bool 的
{
    if (!FeatureLoader.Enabled<CdProtocolFeature>()) return true;   // ✅ 放行原方法
    //                                 return false;  ← ❌ 会把原版行为一起拦掉
    ...
}
```

⚠️ **返回 `false` 整替原方法的钩子尤其要写对**，写错就是弄坏原版。本项目有五处这种：
`Device.Scan` 的时间污染、`CdProtocol` 的两条出站改道、`SpectatorScan` 的旁观者放行、
`ItemManager` 的拦丢弃与拦拾取。

### 11.3 生命周期与状态收尾

| 回调 | 时机 | 要点 |
| --- | --- | --- |
| `Bind(ConfigFile, string)` | 启动时一次 | **与开关状态无关**（热开时配置已经就位） |
| `OnLoaded()` | 启动时一次 | 同上，放一次性初始化 |
| `OnEnabled()` | 「关 → 开」翻转时 | 重建被 `OnDisabled` 撤掉的东西 |
| `OnDisabled()` | 「开 → 关」翻转时 | ⚠️ **有副作用的必须写** |

⚠️ **`OnDisabled` 铁律**：只关不收拾会留下「看得见但没反应」的东西。已经踩过的三类：

- 克隆出来的 UI 控件还在（大厅那一行开关、伪证页的下拉）—— 拨了没反应，反倒像设置坏了；
- 破坏点的小熊还站在地图上，而且「假主人」提示还挂在客户端；
- `CdHello` 的「本次连接已声明过」标记没清 —— 热开之后服务端就不知道谁装了模块。

⚠️ 这些回调由 **Unity 主线程**调（`Plugin.Update` → `ApplyPending`）：`SettingChanged` 可能在
**任意线程**触发（DT_Tools 的 CONFIG 页就是走 HTTP 线程写配置），而回调普遍要碰 Unity API。
DT_Tools 用 `CoroutineHost.Post` 是同一个理由。

⚠️ 有些副作用**不在接管范围内**，只能自己收拾：已排上队的 `PushSurvivalJob` / `PushAfter` 回调
（关掉开关它们照样到点执行 ⇒ 回调第一句必须自校验）、已经注册进 `PacketManager` 的自定义 opcode
（没有反注册 API）、已经写进 `ClueList` / `TextDic` 的数据。

### 11.4 ⚠️ 功能类不能是 `static`

`FeatureLoader.Enabled<T>()` 是泛型，而 **C# 不允许 `static` 类作类型参数**（CS0718）：

```csharp
internal partial class XxxFeature          // ✅
// internal static partial class ...       // ❌ CS0718（2026-10-18 实际编译失败过）
```

成员仍然全部 `static`（与 DT_Tools 的「非 static `sealed class` + 全静态成员」是同一写法）。
partial 的**每一份声明**都要写一致。

### 11.5 子配置也可能不热（易漏）

`ConfigEntry.Value` 是热读的，但**用它算出来的东西不一定是**。两个实际例子：

- `LobbyToggle.RowTitle` 原来只在建行时读一次 ⇒ 改标题要重开设置页。
  现在在 `Sync` 里对齐（原版每次刷新设置页都会调，本来就在主线程；
  **刻意不订阅** `SettingChanged`，理由同 §11.3 的线程说明）。
- 破坏点坐标：客户端 `S_MODIFY_DEVICE` **不更新 Pos**（0.1.18 :27850 的 `Modify`），
  光改服务端 `DeviceInfo.Pos` 那只熊不会挪窝 ⇒ 现在每秒校验一次，不一致就移除重建。

### 11.6 怎么确认生效了

改完开关后看日志（`BepInEx\LogOutput.log`）：

```text
[ClueDetail] 已启用功能 XxxFeature（热更新）。
[ClueDetail] 已停用功能 XxxFeature（热更新）。
```

启动那一行仍是 `启用 N，停用 M，失败 K` —— **失败必须为 0**。

### 11.7 ⚠️ 补丁类必须由装载器**逐个** `PatchAll`（2026-10-18 血的教训）

`harmony.PatchAll(功能类)` **不会**扫这个类型的嵌套类 —— Cecil 实证：
`PatchAll(Type)` → `CreateClassProcessor(type, false).Patch()`，**全程没有 `GetNestedTypes`**。
而本项目的补丁类**全部嵌套**在 Feature 里 ⇒ **一个都挂不上**；
更糟的是 `PatchAll` 对「没找到补丁」**不报错** ⇒ 日志照样写「失败 0」。

**症状**：插件加载一切正常（配置生成、自定义协议注册、镜像到 DT 页都成功），
**但所有功能都没效果** —— 2026-10-18 用户实机报告「小熊不显示、设置项不存在、
线索没补全活动、投出黑幕照常结束」时，根因就是这个。

**改法**（`FeatureLoader.MountPatches`）：枚举本功能名下的补丁类，**逐个 `PatchAll(补丁类)`**
—— 与 DT_Tools 的 `FeatureLoader.Mount` 同款。归属判断要**同时**覆盖两种组织方式：

```csharp
bool belongs = t.IsNested
    ? t.DeclaringType == featureType          // 嵌套在功能类里（本项目）
    : t.Namespace == featureType.Namespace;   // 同命名空间的顶层类（DT_Tools 那样）
```

⚠️ **只比 `Namespace` 会漏掉嵌套类**：嵌套类型在**元数据**里的 `Namespace` 是空的
（Cecil 读出来是 `""`；只有运行期反射才返回外层命名空间）。

**唯一可靠的判据是启动日志里的 `补丁 N 个`**：**N 必须大于 0**。
`失败 0` 只说明没抛异常，**不代表补丁挂上了** —— 这两件事必须分开看。

## 12. 资源与 prefab：bundle 的坑（2026-10-18）

⚠️ **同一个资源地址可能在多个 bundle 里各有一份，只有 `catalog.bin` 指向的那份会被加载。**

实测：`Assets/@Resources/Prefabs/UI/SubItem/UI_ClueSubItem.prefab` 同时存在于
- `common_prefab_assets_all_4253c60d4b003cb4ebd36ee3d5cf745a.bundle`（0.1.18 的，**catalog 指向它**）
- `ui_assets_all_e5028a3ceeca10ef8bbe2f46f263871c.bundle`（上一版**残留的孤儿**：文件还在、
  地址表里也自称有这个 prefab，但 `catalog.bin` 里搜不到它的哈希）

⇒ 孤儿那份的节点结构与 0.1.18 的代码枚举**对不上**（它有 `ClueText`，没有 `ClueWhereText`）。
上一轮会话 dump 的正是它，于是得出了「0.1.18 删了 ClueWhereText / 静态 dump 与运行期事实矛盾」
这个**错误结论**。

**定位某个 prefab 真正在哪**（`findstr` 对 bundle 里的**压缩**字符串不可靠，别用它下结论）：
1. `python .tmps\tools\probe-addressables.py <bundle> <关键词>` —— 用 UnityPy 读 bundle 自带的
   地址表（`env.container`）与 GameObject 名字；
2. `python .tmps\tools\scan-bundles.py <bundle 目录> <关键词>` —— 遍历全部 bundle 列出命中；
3. 拿地址去 `catalog.bin` 里搜 bundle 哈希：**不在 = 孤儿，在 = 真身**。这一步最省事、最硬。

**0.1.18 的 `UI_ClueSubItem` 节点真相**（`common_prefab` bundle）：

```text
UI_ClueSubItem (612x104)
  MousePanel                    整行射线目标（悬停靠它）
  Clue                          612x104
    ClueWho (100x100 @ x=-254)  ClueWhoFrame / ClueWhoMask / ClueWhoImage / ClueWhoText(70x20 @ y=-38)
    ClueContent (100x100 @ 中心)
      ClueIconParent
        ClueIconPanel  (596x88, Image)      整条底图
        ClueWhereImage (150x80 @ x=-135)    房间卡
        ClueWhereText  (140x60 @ x=-135)    房间名（与卡同位置，叠在上面）
        ClueIconLine   (4x80   @ x=-54)     分隔竖线
        ClueWhenText   (140x60 @ x=+71)     时间
    PropositionButton (100x80 @ x=244)  SubmitText / SubmitBlockImage
  Statement                     同构，容器叫 StatementContent/StatementIconParent
  Selected                      选中框
```

⇒ 与代码里的 `Texts`（8 项）/ `Images`（6 项）**完全吻合**；
`ClueText` / `StatementText` / `ClueWhatImage` / `ClueTextParent`（0.1.17a 的旧两视图容器）都不存在。

**活动卡面的精灵还在**：`catalog.bin` 里 146 个 `_card` 地址健在 —— 房间卡 `OperatingRoom_card`
等，活动卡 `WComputer_card` 等，还有 `WShelf_Classroom_card` 这种「活动_房间」组合。
官方只是**不再用它**，资源没删。

⚠️ 取用走 `Managers.Resource.Load<Sprite>("键.sprite")`（:36766），它是**查预载字典**，
不是按需加载：预载按 Addressables 标签整包做（`School` / `Lobby` / `Tutorial`，:94647 / :94651），
**没预载的键会静默拿到 null**。所以「拿不到卡面」有两种原因（资源删了 / 没预载），
`ClueCard` 对每个缺失的键只记一条 Warn，便于区分。
