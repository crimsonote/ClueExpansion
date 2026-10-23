# 游戏 API 锚点（0.1.18）

## 5. 游戏 API 锚点（0.1.18）

反编译口径：`.tmps\decomp\Assembly-CSharp-0.1.18.decompiled.cs`（183351 行，2026-10-16）。
引用一律带行号，游戏升级后按这些行号逐条复核。

**基准指纹**（2026-10-18 记录，用来判断"游戏是否已升级"）：

| 项 | 值 |
| --- | --- |
| `Assembly-CSharp.dll` 文件时间 | 2026-10-16 16:37:19 |
| MVID | `baf64ef4-e6fb-4b89-b45c-3913dcfd481a` |

**2026-10-23 复核（游戏在 10/23 17:23 更新过）**：

| 项 | 值 |
| --- | --- |
| `Assembly-CSharp.dll` 文件时间 | 2026-10-23 17:23:25 |
| MVID | `da5eed19-3552-4296-b5e3-0e3e157cbefc` |
| 大小 | 2927616 字节 |
| SHA256 | `D82A29BC2BEE40C487F66F4DB6213B52CF7CD683BB50460E6244BE4C4A2643CB` |
| 新版全量反编译 | `.tmps\decomp-20261023\Assembly-CSharp.decompiled.cs`（170692 行，2026-10-23） |

⚠️ 上面的**行号锚点仍按 0.1.18 旧反编译**（`tools/check-targets.ps1` 已在新 DLL 上全量 PASS，
说明补丁目标都没失效）。新版 dump 的用途是**查新增功能**，不是替换旧行号 —— 两者行数不同
（170692 vs 183351），行号不能混用。

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

**补充锚点（2026-10-18 夜，隐蔽破坏点二改期间核对；`ClearFuseboxSabotage` / 线索双源那一批见 §10.1 与 §10.6）**：

| 目标 | 行号 | 用途 |
| --- | --- | --- |
| `DeviceManager.ClearFuseboxSabotage` / `Fusebox.ClearSabotage` | 166838 / 165677 | 关电闸窗口（撤图钉 / 平板条目） |
| `DeviceManager.StartFuseboxSabotage` / `Fusebox.StartMission` | 166828 / 165669 | 开电闸窗口 |
| `DeviceManager.GetDisconnectFuseCount` | 166857 | 还有几个电箱没恢复供电 |
| `AreaManager.RefreshLight` 的 case 0 | 176585 | 原版那条「供电全恢复 + 60 秒」 |
| `Fusebox.DisconnetCable` | 165644 | 拉闸（要求 `MissionType == -1`） |
| `Managers.Device.SearchInteractDevice` / `DeviceBase.IsValidDevice` | 28300 / 4939 | 客户端「按 E」目标选择（看 `Data.Interacts` 矩形） |
| `Summon.Init` | 3908-3955 | 小熊交互框 = prefab `Interact` 碰撞盒（±75）+ `CanUseDarkness = true` |
| `DeviceBase.HasDestroyableClue` / `ClueMirror` / `PacketHandler.Handle_S_ADD_CLUE` | 5021 / 180791 / 43621 | 线索在房主 / 远端的**两套真值源**（§10.6） |
| `LocalLoopbackSink` | 162611 | 房主自己那条回环会**重新序列化**（≠共享引用） |
| `Server.Game.HostPeerSession.Send(IMessage)` / `.Underlying` | 175626 / 175587 | **服务端给每个玩家的下发都经过它**（远端 = `SteamP2PSession`，房主 = `LocalLoopbackSink`）⇒ 时间污染在这里改写出站副本 |
| `S_ADD_MY_CLUES` 发送点 | 174520-174540（另见 :173755 旁观者） | 平板主列表 / 调查开场推送 —— **界面时间真正的来源** |
| `Device.Scan` 的 `AddRange(ClueList)` | 166318 | 扫描下发**按引用**（要改必须先克隆） |
| `ClueMirror` 的读取方 | 5021-5038 / 181503-181521 | 只有 `HasDestroyableClue`（不读 Time）与迁移快照读它 —— **UI 从不渲染** |
| `ClueManager.Clue.CompareTo` | 27536-27539 | 客户端线索按 `Time` 排序（冻结时间会改变顺序，已接受） |
| `Server.Game.Player.InteractLock` | 178386 | 交互锁 500ms（`DeviceManager.Interact` 静默丢弃期间的重发） |


`Core/LocalizationData.cs` 是**生成文件**（46 条词条 × 5 语言），
来源 `.tmps/gamedata/TextData.json`（0.1.16b 基线），不要手改。

