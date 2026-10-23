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
   绝不动服务端 `ClueList`（依据见 `docs/隐蔽破坏点.md` §2 末行与 §4 之 4；
   代价与"房主看不到时间污染"这个已知缺口见 `docs/线索机制.md`）。
2. **未装插件的一端必须不炸。** 跨端数据分**两条通道**，按数据性质选，不是二选一（2026-10-18 定案）：
   - **线索的「详细活动」→ protobuf 未知字段**（`Core/UnknownFields.cs`，字段号 **100**）。
     它是**线索的一个属性**，附着在 `PropositionInfo` 上天然一一对应：零关联键、无时序要求、
     不会出现"同房间同时刻两条线索撞键"。
     ⚠️ **写入必须早于 `Clone()`** —— `Device.Scan` 改的是 `ClueList` 里的对象引用
     （随后 `AddRange` 进包），而 `RecordLastUsingPlayer` 是"先 `Clone()` 再 `Broadcast`"，
     所以那条路只能在 `GameRoom.Broadcast` 的 Prefix 里、对着包内的 `Info` 补。
     ⚠️ 字段号**刻意不用 4 和 6**：4 是官方刚弃用的号（可能回收），6 紧邻 `Destroyed=5`
     （"下一个新字段"最可能挑的）。100 落在明显没人用的租用区（**101** 后来给了破坏点的
     「显示时刻」标记，见 `docs/线索机制.md`）。
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


## 文档地图（机制地图在 `docs/`，本文件只放规则）

⚠️ 本文件要**常年驻留上下文**，所以只保留「怎么做 / 不能怎么做」的规则与索引；
机制地图、行号锚点、调查结论一律放 `docs/`，需要时再读（本仓库唯一豁免的规则仍在本文件）。

| 想查什么 | 去哪 |
| --- | --- |
| **全局俯瞰：分层 + 破坏点时序 + 线索双源（HTML+SVG，英文名带中文标注）** | `docs/系统架构图.html` |
| 游戏 API 行号锚点、基准指纹、反编译口径 | `docs/游戏API锚点.md` |
| 隐蔽破坏点：破坏 / 修复 / CD 封锁 / 线索污染 | `docs/隐蔽破坏点.md` |
| 线索系统（入口）：房主的真值源 vs 远端、污染可见性、30 秒去重窗口 | `docs/线索机制.md` |
| **线索文本管线（详表）**：文本来源优先级阶梯、渲染路径、污染逐段影响、0.1.17a↔0.1.18 差异、决策记录 | `docs/线索管线.md` |
| 审判规则：黑幕二次投票 | `docs/审判规则-二次投票.md` |
| **游戏内聊天命令**：`/say` 私聊、`/clue` 线索检索、`/help` 报到式帮助、会议自动线索报告、与其它模块的共存约定 | `docs/游戏内命令.md` |
| prefab / bundle 的坑与节点真相 | `docs/prefab与bundle.md` |
| **彩蛋：尸体撒花**：四层判定、`Corpse.RoomID` 恒 0、`CompositeCollider2D` 陷阱、离线模拟手法 | `docs/彩蛋撒花.md` |
| 一次性调查、被证伪的方案、脚本 | `D:\git\DT_Tools\.tmps\`（**不进任何仓库**） |

## 5. 游戏 API 锚点（0.1.18）→ `docs/游戏API锚点.md`

基准：`0.1.18`（反编译口径与全部行号锚点见该文档）。**引用游戏 API 之前必须逐个核对行号**；
私有成员用字符串定位（Traverse / AccessTools），并在注释里带 `0.1.18 :行号`。
判断基准是否还成立，最快的办法是重跑 `tools/check-targets.ps1`。
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
4. ~~伪证 UI 还原~~ **已做（待实机）**：`Features/Client/LieWhat/` —— 还原旧版的**整行**「活动」。
   ⚠️ 2026-10-18 修正：早前只克隆了一个下拉、位置还压在别的行上；现在按旧版 prefab 的真实结构还原。
   旧版（0.1.17a，从上一版残留的 bundle 里读到）是 `LieList` 下
   `LieWhereText`(y=301.9) / `Who`(y=180) / `When`(y=50) / **`What`(y=-80)** / `LieClueParent`(y=-208)，
   `What` 行内 = `LieWhatText`(300x50 @ (-250,+50)) + `WhatDrop`(**550x60**)；
   0.1.18 删掉 What 行后把 Who/When 上移（140 / -40），`LieClueParent` 没动。
   现在：克隆 `Who` 整行 → 立刻改名（原版 `Bind` 是按名字递归找的）→ 放回 `When` 下方 110 →
   标题取本模块词条 `Localization.WhatLabelKey`（0.1.18 连 `What` 词条一起删了）→
   `RemoveAllListeners()` 后重绑。选项用 `RoomDeviceScan`（旧版是已删除的 `ClueManager.RoomObjectDict`）；
   **房间里没有可调查设备时列出全部活动**（旧版是干脆不许在这种房间伪造，0.1.18 改成只看服务端
   下发的 `LieRoomSet`（`CanLieInRoom` :53713）——房间准入仍按服务端口径，本模块不替它收紧）。
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

7. ~~隐蔽破坏点~~ **已做，2026-10-18 夜二次定案**：`Features/Host/ShadowSabotage/` —— 两个伪装成小熊的
   服务端破坏点（科学室的身份识别组件 / 电脑室的时钟记录组件）。新口径：破坏期间**封锁一切其他破坏**
   （另一个点 + 原版电闸窗口，图钉 / 平板条目一起撤掉）；**修复完成 60 秒后**自建计时器重派发；
   **每次完整破坏追加一条**「黑幕…切断了电力」线索（同 30 秒窗口合并）；黑幕可修自己做的破坏。
   ⬜ **待办**：① 科学室的落点要挪到"最左侧但玩家真能站进去"的点（挪完按 `docs/隐蔽破坏点.md` §4 之 7
   的 MCP 手法截图验证）；② 时间污染已改成「写入时打未知字段 101 + `HostPeerSession.Send` 出站副本替换」
   （房主与远端都可见，真值 / 30 秒去重 / 迁移不受影响），**待实机复验**；详见 `docs/线索机制.md`。
   机制、坑、验证状态：`docs/隐蔽破坏点.md`。

8. ~~线索卡片回退旧版 UI~~ **已做（待实机）**：`Features/Client/ClueCard/`。
   0.1.18 重做了 `UI_ClueSubItem` prefab（见 §12），把旧版的两格删掉了 ——
   「活动卡面（设备图像）」与「整句文本」，只剩「房间卡 + 房间名 + 分隔线 + 时间」。
   本功能在运行期补回那两格：活动卡面克隆 `ClueWhereImage`、整句克隆 `ClueWhereText`，
   悬浮（`OnEnterClue` / `OnExitClue`）时藏起图标组、把整句显示出来。
   版式（各栏位置与宽度）**全部是配置项**（段 `[ClueCard]`），改 `.cfg` 就能调，不必重编。
   ⚠️ 伪证行（`Statement*`）也一并支持，但它的版式是**按空档自动排**的（没有配置项）。

9. ~~线索合并窗口可配置~~ **已做（待实机）**：`Features/Host/ClueMerge/` —— `[ClueMergeWindow] Seconds`：
   `30`（默认）= 原版桶长，改 `.cfg` 立刻生效；`0` = 禁用合并（每次使用都新起一条）；
   其它值 = 自定义滚动桶长。**2026-10-20 起总是接管** `Device.RecordLastUsingPlayer`（逐字复刻，
   含 `S_REMOVE_CLUE`＋`S_ADD_CLUE` 顺序与 `S_LEFT_CLUE`），并显式补两个 Postfix 的职责
   （字段 100 / 101 / 103）。桶长算术在 `Core/ClueMergeWindow.cs`，破坏点自建的那条线索也走它。
10. ~~身份污染：当事人看实名~~ **已做（待实机）**：`UnknownFields.RealOwnerFieldNumber = 103`
   （**只存在于服务端真值**）＋ 出站口按收件人改写（`ShadowSabotage/Wire.cs` 的 `RewriteOutflow`）：
   当事人拿到真实 `PlayerId`（看到自己的名字与头像），其他人拿到 `0`（「某人」），
   未装本模块的端同样按包生效；出站副本一律把 103 清零（防"别人自己读出来"）。
   范围：`S_SCAN_DEVICE` / `S_ADD_MY_CLUES` / `S_ADD_CLUE` / `S_REMOVE_CLUE`；
   **角色线索 11037/11038/66613 与非污染线索一律不碰**。完整表格见 `docs/线索管线.md` §12。


11. ~~命令桥 + 实验性破坏点命令~~ **已做（待实机）**：`Features/Both/CommandBridge/`。
    与捉迷藏 `Console/ConsoleBridge.cs` 同款两步：① Prefix 挂
    `DT_Tools.WebConsole.WebConsole.ExecuteCommandText(string)`（**2.0.0.6 实测仍在**）拦截执行；
    ② 重建 `CommandsApi._cachedJson` 让命令进列表与补全（**纯文本追加**上游原表，不引 JSON 库）。
    命令：`cd_sabotage <clock|identity|both> <on|off|status>` —— 不经玩家操作直接翻转破坏点，
    收口与玩家交互完全相同（`Finish` → 广播 / 封锁 / 箭头 / 11037 线索 / 修复后 60 秒重派发）。
    ⚠️ `Finish` 已改为**容忍 `player == null`**（命令没有操作者，进度条那步跳过）。
12. ~~DT CONFIG 页的模块标签~~ **已做（待实机）**：`Core/Dt/DtBridge.cs` 的 `TryRegisterTag`。
    给 `FeatureTagCatalog.Entries` 追加一条 `{Tag, Key, Group, Label, Description}`（`Key = ClueDetail`、
    显示名「线索拓展」）、把占用的位并进 `AllBits`、再写 `Engine.SectionTags` 与 `Engine.SectionGroups`；
    标签位取 `1 << 39`（上游 `FeatureTag` 是 `[Flags]`，已定义到 `Peripheral = 1 << 38`）。
13. ~~游戏内聊天命令~~ **已做（待实机）**：`Features/Host/GameChat/` —— `/say` 私聊（大厅与会议）、
    `/clue` 线索检索（仅会议，支持 `t:`/`p:`/`r:`/`a:`/`-排除`/裸词）、**`/who` 列出 ID 与昵称**、
    `/help` 报到式帮助（**不吞**，让捉迷藏等模块继续报到）；另含**进入会议时给每人自动发一份自己的线索报告**
    （窗口 = 案件时间前 20 秒）。**两个补丁点**：
    ① `Server.Game.HostPacketHandler.Handle_C_CHAT_MESSAGE`（:177661），必须是 `Priority.First`
    （捉迷藏同点、默认优先级且会吞 `/help`）；
    ② `Server.Game.HostPacketHandler.RelayNormalChat` —— **兜底吞掉 `/help`**：我们为了"报到"必须
    `return true`、无法自己吞，原版于是把它当普通聊天广播给全服；这里只拦**完全等于 `/help`** 的文本。
    只处理 `NormalChat`，发信机频道一律放行。
    ⚠️ 颜色做不了：客户端 `Util.NeutralizeRichText`（:43911）会杀掉所有 `<color>`；单条最多 3 行，
    超宽由 `[GameChat] MaxLineWidth`（默认 50，中文算 2 / ASCII 算 1）折行 —— 不折会被 TMP 直接截断。
    机制与约定见 `docs/游戏内命令.md`。
14. ~~破坏点修复时的"他人可见"~~ **已做（方案 A，待实机）**：需求是「白方修小熊时，让其他人知道他在修」。
    实现：修理 / 破坏的**进度期间**，服务端把操作者置 `EPlayerState.Casting`（`SetBusyState`），
    其他人就看到 `8_Interact`（蹲下操作的循环动作）；`Finish` / `EndSession` 恢复 `Idle`。
    机制依据：服务端 `Player.State` 的 setter（:178630）**自动广播** `BroadcastModifyPlayer(ChangePlayerState)`
    → 客户端 `State` setter（:12525）→ `UpdateAnimation()`。
    ⚠️ 关键细节：`EPlayerState.Interact` 在 `UpdateAnimation`（:13466）里与 Idle/Run/Carry 同分支
    （**播 Idle，没有操作动作**），真正带"操作"动作的只有 **`Casting` → `8_Interact`**（:13488-13489）；
    服务端 `StartState/EndState` 对 `Casting` **无副作用**（只有 Idle/Run 刷速度、Hide/Sit 锁技能掉物品）。
    我们的破坏点走 `C_INTERACT_SUMMON`、不经 `C_MODIFY_PLAYER` ⇒ 客户端自己不会设状态，必须服务端主动设。
15. ~~`/help` 广播兜底从未挂载~~ **已做（待实机）**：`HelpBroadcastGuard` 原先二级嵌套在
    `ChatCommandHook` 里 ⇒ 装载器扫不到 ⇒ `/help` 一直进公聊（2026-10-20 实测确认）。
    现已提到 `GameChatFeature` 直接嵌套；`/clue` 同步加了 `id:N` 精确搜索（**污染行对非当事人不参与匹配**，
    与渲染层 `WhoOf` 同口径，见 `docs/游戏内命令.md` §7.1），`/clue help` 3 个气泡 × 3 行。
    见 `docs/游戏内命令.md` §7。

16. ~~身份污染改为「真值保留真实、出站按收件人匿名」~~ **已做（待实机）**：2026-10-20 用户口径 ——
    「每个人记录的**自己的**线索是真实的，只有别人搜集到的才匿名」。旧实现把 `state` 改成
    `Anonymity` ⇒ 真值 `PlayerId = 0`，代价有两个：① 当事人自己的线索被 `StartDetective`
    的 `PlayerId == 自己` 过滤掉（:174525）⇒ 初始列表里没有；② 同一设备同一 30 秒桶内
    **所有人**的线索被原版合并成一条（都等于 0，:166405）。
    现在「某人」档不改真值，只写 103 标记，由 `RewriteOutflow` 按 `recipient == owner ? owner
    : PlaceholderPlayerId` 匿名；`Black` / `Mastermind` 两档仍改真值（那是嫁祸给角色）。
    配套：`StampPollution` 在非污染时会**清除** 101/103（防修复后残留）；
    `ClueMerge` 改为**总是接管** `RecordLastUsingPlayer`，合并判据加「污染标记相同」
    （`SamePollution`）⇒ 破坏前 / 污染期 / 修复后三类记录互不覆盖。

17. ~~维修改为「人人独立进度条、先到先得」~~ **已做（待实机）**：`BreakerState` 的
    `SessionPid` / `SessionStartAt` / `BarOn` / `LastBar` 换成 `Sessions`（pid → 开始时刻）
    与 `LastBar`（pid → 刻度）两个字典；`ScheduleBar` 每人一条 100ms 自续链；
    `BaseProgress` 仍是**共享起始进度**（等价原版电闸 `StateList[2]` 的最大 tick），
    谁中断谁把自己的进度并进去。对齐原版电箱：客户端各自 `StartCasting`，服务端只取最大 tick。

18. ~~断电箭头分阵营通知~~ **已做（待实机）**：`[ClueShadowSabotage] NotifyBlackDelay`（默认 0）
    与 `NotifyWhiteDelay`（默认 12）。破坏发生后黑方 / 黑幕立即收到箭头，白方延迟 N 秒。
    `BroadcastArrow` 改为逐人单发（原来走 `BroadcastAlivePlayers` 全体）。

19. **2026-10-21 五项**（构建/verify 已过，**待实机**）：
    ① **「销毁了证据」卡面**：`ClueWhat.ResolveForCard` 对 11038/66613 不再返回 `WNone`，
       改为按 `S_ADD_CLUE` 的 `DeviceId`（`ActivityMap`）取**被销毁那台设备**的 `WhatType` 出图；
       房间只剩一种活动时兜底。文本仍保持原版「销毁了证据」。
    ② **私聊死者**：`/say` 拒绝把死者当目标；死者发出的私聊与死者收到的回执都带
       `S_CHAT_MESSAGE.IsDead = true`（`SendChat` / `SendBubbles` 新增 `isDead` 参数）。
    ③ **电闸互锁**（真 bug）：先拉一台电闸再破坏小熊会把两个点**永久锁死**。
       根因与修法见 `docs/隐蔽破坏点.md` §7.1 —— `ReopenWindow` 不再因
       `GetDisconnectFuseCount() > 0` 直接返回，改为 `ForceRestoreFuses` 把未恢复的电箱
       强制复位（先置 `Area.IsLight = false` 打开 `ConnetCable` 的守卫）。
       **方案 B（小熊与电闸绑一起修）已评估、不采用**：它必须让那间房变暗才修得了，
       观感与语义都不合适，记录在 §7.1。
    ④ **撒花固定密度**：`[ClueFlowerCarpet]` 新增 `Density`（每万平方单位朵数，默认 0.8）
       与 `MaxFlowers`（默认 400），`PerCorpse` 删除；朵数 = `min(圆面积, 房间面积) × Density / 10000`。
       机制见 `docs/彩蛋撒花.md` §2.1。
    ⑤ **日志开关**：基础设施段 `[Log] Enabled`（默认开）控制 `Log.Info`，
       `Warn` / `Error` / `Exception` 不受影响。热读，改 `.cfg` 立即生效。

### 搁置（等用户决策，2026-10-20）

- **头像与「某人」文本无法同时在纯服务端成立**：`UI_ClueSubItem.FillText`（:86635）
  的文本与头像都取 `Info.PlayerId`，**没有独立头像字段**；而 `Someone_SD` 未注册进
  `catalog.bin` ⇒ 头像会保留上一条线索的真人图。两条路：接受现状（预热是 best effort），
  或允许客户端补 5.5 KB 的 `Someone_SD.png`（`Assets/Someone_SD.png` 已备）。
- **「送书 / 画线」这类同一设备的两次不同交互无法拆分**：两者都在 `RuneStand`
  （`WhatType = 26 = WRune`），活动相同，且 `{what}` 只认 `WhatType`。
  要拆必须给 `PropositionInfo` 加一个「交互种类」维度（新未知字段），
  并让服务端合并判据与客户端 `ActivityKey` 都按它分组。
## 7. 过程材料

- **耐久结论**（机制地图 / 行号锚点 / 踩过的坑）写进 `docs/`，并在上面的「文档地图」里登记一行；
  **`AGENTS.md` 只放规则与索引** —— 它常年驻留上下文，别把它撑大。
- **一次性调查、被证伪的方案、临时脚本**放 `D:\git\DT_Tools\.tmps\`（**不进任何仓库**）。
- 改了行为要同步改三处：`docs/` 对应文档、本文件的规则 / 待办、以及代码注释里的章节引用
  （重排章节号时全局搜 `AGENTS`）。

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

**实测口径：游戏里的 DT_Tools 是 `2.0.0.6`**（2026-10-19 用 Cecil 重核，与 10-18 记的 2.0.0.4 已不同 ⇒ 下面这张表**只看 §8.1**）：

| 集成点 | 2.0 状态 |
| --- | --- |
| 配置入口 `DT_Tools.Core.Engine.Config` | ✅ 仍在（public static 属性） |
| 命令桥-执行 `WebConsole.ExecuteCommandText(string)` | ⚠️ **已降为 `private static`**（类名与签名都没变）⇒ 仍可行，挂载改用字符串名（HideAndSeek 就是这么做的） |
| 命令桥-列表 `CommandsApi._cachedJson` | ✅ **仍在**，类移到了 `DT_Tools.WebConsole.Api.CommandsApi` |
| `SectionCategories` | ❌ 已删除；替代品是 `SectionGroups`（段名→组）+ `SectionTags` + `SectionSides` |

**结论（2026-10-19 已落地）**：
- **标签登记已做** —— 走新的标签体系（`Engine.SectionTags` + `FeatureTagCatalog.Entries`），
  见下方 §8.1 与 `Core/Dt/DtBridge.cs` 的 `TryRegisterTag`；
- **命令桥已做** —— 见 `Features/Both/CommandBridge/`（执行口 + 列表缓存重建）；
- 实验性命令 `cd_sabotage` 用它把破坏点强制开 / 关，详见 `docs/线索管线.md` §13。

⚠️ **核对这类"上游接口变没变"时**（2026-10-18 我连错两次，记下来）：
① 查类型**别用全名精确匹配** —— 2.0 把 API 层整体挪进了 `DT_Tools.WebConsole.Api.*`，
精确查会得到"不存在"的假阴性，要用**后缀匹配**；
② 查成员**别过滤可见性** —— `Where-Object { $_.IsPublic }` 会把降级成 `private` 的成员藏起来；
③ **终极依据是调用方代码里的字符串常量**（HideAndSeek 的 `ConsoleBridge.cs` 顶部那 7 个
`Dt*TypeName`），DLL 扫描说"不在"时先核对常量写的是什么。详见 HideAndSeek/AGENTS.md §6 末节。


### 8.1 部署版 DT_Tools 的真实接口（2026-10-19 用 Cecil 重核 2.0.0.6）

⚠️ 上一轮记的"命令桥挂点已不存在 / `SectionCategories` 已删除"**只对了一半**，原因是查类型时
**用全名精确匹配 + 过滤可见性**（两条都会造成假阴性，见本节末第 ①② 条）。用 Cecil 读部署 DLL 的真实形状：

| 成员 | 2.0.0.6 状态 | 用途 |
| --- | --- | --- |
| `WebConsole.ExecuteCommandText(string)` | ✅ 在（`internal static CommandResult`） | 命令桥的执行口 |
| `CommandsApi._cachedJson` | ✅ 在（`private static string`） | 命令表缓存：**首次请求才生成** ⇒ 我们要重建 |
| `CommandResult.Success(object)` / `Fail(string,object)` | ✅ 在 | 回包（反射构造） |
| `Log.Info(string tag, string msg)` | ✅ 在 | 把命令输出写进 DT 日志流（否则人看着"没返回"） |
| `Engine.SectionCategories` | ❌ **确实已删** | 旧版的「分类」 |
| `Engine.SectionTags : Dictionary<string, FeatureTag>` | ✅ 在 | 现在靠它归标签 |
| `Engine.SectionGroups : Dictionary<string, string>` | ✅ 在 | 分组名 |
| `Engine.SectionSides : Dictionary<string, FeatureSide>` | ✅ 在 | 作用面 |
| `FeatureTagCatalog.Entries : IReadOnlyList<Entry>` | ✅ 在 | 标签的显示信息；`ConfigApi.HandleTags` 直接投影它 |
| `FeatureTag`（`[Flags]`，已定义到 `1 << 38 = Peripheral`） | ✅ 在 | 我们用 `1 << 39` 追加一个新标签 |

⇒ 结论：**命令桥可行**（已实现），**标签要自己追加**（`FeatureTagCatalog.Entries` + `Engine.SectionTags`，
两个都是运行期可写的静态成员），不再有"分类字段"可写。
## 9. 审判规则：黑幕二次投票 → `docs/审判规则-二次投票.md`

第一次投票若投出黑幕：不作终局结算，把黑幕标为本轮不可投并立即重开一轮（不重开讨论）。
机制依据、改道点、已接受副作用全在该文档里。

## 10. 隐蔽破坏点 → `docs/隐蔽破坏点.md`

两个伪装成小熊的服务端破坏点（科学室身份识别组件 / 电脑室时钟记录组件）。
**规则摘要**：未破坏时只有黑幕能按 E；破坏后所有人（含黑幕）可修；
**破坏期间封锁一切其他破坏**（另一个点 + 原版电闸窗口，图钉 / 平板条目一起撤掉）；
**修复完成 60 秒后**自建计时器重派发；**每次完整破坏追加一条**「黑幕…切断了电力」线索（同 30 秒窗口合并）。
实现细节、机制依据、坑与实机验证状态见文档。
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

### 11.2 入口检查（硬要求，本项目共 38 处）

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

⚠️ **返回 `false` 整替原方法的钩子尤其要写对**，写错就是弄坏原版。本项目有八处这种：
`CdProtocol` 的两条出站改道、`SpectatorScan` 的旁观者放行、`ItemManager` 的拦丢弃与拦拾取、
以及 `ShadowSabotage` 的 `DisplayTimeOutflowHook`（自己发改写后的包、拦掉原包 ——
⚠ 必须走 `__instance.Underlying.Send`，直接再调 `Send` 会重入）。
以及 `ClueMerge` 的 `MergeWindowHook`（**2026-10-20 起总是接管** `Device.RecordLastUsingPlayer`：
原版判据只看 `PlayerId + 30 秒桶`，不区分「破坏前 / 污染期 / 修复后」⇒ 会互相覆盖；接管后额外要求「污染标记相同」（`SamePollution`）。接管时必须显式补两个 Postfix 的职责：
`RecordDetailFeature.RecordHook.StampWhat`（字段 100）与 `ShadowSabotageFeature.StampPollution`（字段 101/103））。

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


## 12. 资源与 prefab：bundle 的坑 → `docs/prefab与bundle.md`

⚠️ **同一个资源地址可能在多个 bundle 里各有一份，只有 `catalog.bin` 指向的那份会被加载**
（上一轮就是 dump 了孤儿 bundle 才得出错误结论）。定位手法与 `UI_ClueSubItem` 的节点真相见文档。