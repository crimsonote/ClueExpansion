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

**2026-11-02 复核（当前安装）**：

| 项 | 值 |
| --- | --- |
| `Assembly-CSharp.dll` 文件时间 | 2026-10-26 13:08:20 |
| 大小 | 2,838,016 字节 |
| MVID | `baf64ef4-e6fb-4b89-b45c-3913dcfd481a` |
| SHA256 | `91E789EA8EF30D00F8C5D714FADFEFBEB838AD981640FFC04389870F931D12B5` |
| 反编译转储 | `.tmps\decomp\Assembly-CSharp-0.1.18.decompiled.cs`（4,840,172 字节） |

当前安装 DLL 与 0.1.18 转储**逐字节一致**（转储 SHA256
`43D82256A1672631A38C3C1B8595DB3067CEEED89D41D403CDEFC8DA0759E675`）。
2026-10-23 的 `da5eed19…` 是那次临时更新的构建；当前安装已回到 `baf64ef4…` 的 0.1.18 形状，
行号锚点继续按下表使用。

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


## 11. 任务满报告延迟 / 两次选人（2026-11-02）

> 机制、阶段图与实测清单见 `docs/任务满报告延迟与两次选人.md`。

### 11.1 任务满报告延迟

| 目标 | 行号 | 用途 |
| --- | --- | --- |
| `MissionManager.TriggerAllClearEnd()`（private，internal 类型） | 169787 | 唯一拦截点：报告尸体 / 白方胜利 |
| `MissionManager.Instance` / `.AllClear` | 169152 / 169168 | 反射读任务满状态 |
| `GameRoom.FindOldestUndiscoveredCorpse()` | 174404 | 取最老未发现尸体 |
| `Corpse.DiscoverByTimeOver()` | 171977 | 原版报告 |
| `Corpse.DeviceInfo`（继承自 `Server.Game.Device`）/ `StateList[5]` | — | 自动报告剩余 |
| `GameRoom.SurvivalTick()` | 174298 | `AllClear` 后直接 return，倒计时天然停住 |
| `TimeManager.UpdateRemainTime(float)` | 181703 | 推迟期间冻结倒计时 |
| `TimeManager.TickSurvive()` | 181688 | 冻结兜底 |
| `TimeManager.ResetSurvival()` | 181784 | 换局复位 |
| `GameRoom.ChangeGameState(EGameState, EGameState)` | 173057 | 阶段切换复位（手动报告） |
| `GameRoom.FullReset()` | 172984 | 房间复位 |
| `GameRoom.PushAfter(int, Action)` | `Server.Game.JobSerializer` 扩展方法 | 推迟 job |

### 11.2 两次选人

| 目标 | 行号 | 用途 |
| --- | --- | --- |
| `GameRoom.PickCharacter(Player, int)` | 174189 | 模型阶段放行 / 技能阶段截住 |
| `GameRoom.PickCharacterTick()` | 174091 | 40 秒强制分配 / 技能超时 |
| `GameRoom.CheckPickAllDone()`（private） | 174241 | 压住原版提前收尾 |
| `GameRoom.CancelPickCharacter(Player)` | 174249 | 技能阶段禁止取消 |
| `GameRoom.StartPick()`（private） | 174064 | 新一轮复位 |
| `GameRoom.EnterPlayer(Player)`（private） | 175243 | 技能阶段中途加入 |
| `GameRoom.HandleLeavePlayer(HostPeerSession, Player, bool)` | 173958 | 离场释放技能 |
| `GameRoom.Broadcast(IMessage, int)` | 174880 | 只拦模型阶段 40 秒的 `S_TIME{0}` |
| `TimeManager.SetStopWatch(int)` / `.StopWatch` / `.RemainTime` | 181712 / 181672 / 181670 | 技能阶段倒计时 |
| `SkillComponent.AllocateSkill(CharacterData)` | 180207 | 装技能（含 `ChangeSkill` / buff / `SkillState`） |
| `SkillComponent.DeallocateSkill()` | 180282 | 释放默认技能 / 旧 buff |
| `SkillComponent.UseRuleBreaker(int)`（private） | 180672 | 复制目标实际技能 |
| `UI_PickPopup.PickCharacter(S_PICK_CHARACTER)` | 65295 | `PlayerId=0` 只变灰；`PlayerId=自己` 锁提交/回头像 |
| `UI_PickPopup.CancelPickCharacter(S_CANCEL_PICK_CHARACTER)` | 65330 | `PlayerId=0` 只点亮；`PlayerId=自己` 复位提交/清头像 |
| `UI_PickPopup.SetTime(int)` | 65363 | `time<=0` 时 `_pickClosed=true`，按钮永久隐藏 |
| `UI_PickPopup.OnEnable()` | 65390 | 客户端首次打开时的完整重置（服务端无法直接调） |
| `UI_PickSubItem.Pick(int)` / `.RefreshUI()` | 88819 / 88826 | 变灰 / 点亮 |
| `Server.Game.Player.IsDummy` / `.IsSpectator` / `.OwnedCharacterIds` | — | 非参与者判定 / 随机候选优先级 |

`Core/LocalizationData.cs` 是**生成文件**（46 条词条 × 5 语言），
来源 `.tmps/gamedata/TextData.json`（0.1.16b 基线），不要手改。

