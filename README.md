# 线索拓展（ClueDetail）

《Deadly Trick》的独立 BepInEx 5 插件。

**一句话**：把 0.1.18 从线索提示里删掉的「详细活动」那一段找回来 —— 让线索重新显示成
旧版的 `XX 在 YY 于 ZZ 完成了 WW`。

---

## 1. 为什么需要它

0.1.18 重做了线索/证据子系统，为了让线索里的"设备动作"不再泄露信息，**四处同时**移除了那一段：

| 层 | 0.1.16b / 0.1.17 | 0.1.18 |
| --- | --- | --- |
| 协议字段 | `PropositionInfo.WhatType`（字段号 4） | **删除**（字段号未复用） |
| 句子模板 | `PropositionSentence<角色>` 含 `{what}` | 去掉 `{what}`，只剩 `{where}` |
| 语言表 | 46 条 `W*` 词条（`WComputer` = "使用了终端机"…） | **五个语言表全部 0 条** |
| 渲染逻辑 | `GetText(clue.Info.WhatType.ToString())` | `GetRoleWhatKey(PlayerId)`，恒为"切断了电力/销毁了证据" |

于是旧版那句 `…于{地点}使用了终端机。` 变成了 `…位于{地点}。`。

## 2. 本插件怎么还原

不改任何原版字段、不加新 opcode、不动未装插件的客户端：

- **服务端**：线索写入的瞬间把设备表里的 `Data.DeviceData.WhatType` 记进旁路表，
  在下发（`S_ADD_CLUE` 广播 / `S_SCAN_DEVICE` 扫描）时写进 `PropositionInfo` 的
  **protobuf 未知字段（字段号 4）**。
  原版客户端解析时会把未知字段原样保留并忽略 —— 行为与原版**完全一致**。
- **客户端**：装了本插件的客户端从这个字段读回活动类型，并把 46 条 `W*` 词条
  注入回语言表，然后把活动拼回句末标点之前。
- **房主没装时**：客户端退化为自采集 —— 扫描时记下"这次扫的是哪台设备"，
  用 `Data.DeviceDic[deviceId].WhatType` 反查。

## 3. 构建与部署

```powershell
cd ClueDetail
dotnet build -c Release --nologo
pwsh -File verify.ps1          # BOM / 乱码 / 禁止项 / 构建
pwsh -File deploy.ps1          # 复制到游戏（游戏需先退出）
```

游戏不在默认路径时：`dotnet build -c Release -p:GameDir="D:\...\Deadly Trick"`。

部署后**必须重启游戏**，并在 `BepInEx\LogOutput.log` 里确认：

```text
[ClueDetail] 0.1.0 加载完成：启用 2，跳过 0，失败 0。
```

`失败` 必须为 0 —— 补丁挂不上不会报错，只会静默失效。

## 4. 配置文件

`BepInEx\config\ClueDetail.cfg`，一段一个功能：

| 段 | 说明 |
| --- | --- |
| `[ClueRecordDetail]` | 房主侧：记录并下发详细活动 |
| `[ClueRenderDetail]` | 客户端侧：还原词条与句式、自采集设备关联 |

## 5. 现状

全部功能已实现，**尚未进游戏验证**：

| 功能 | 位置 |
| --- | --- |
| 补丁框架（`[PatchFeature]` + 段开关自动绑定） | `Core/FeatureLoader.cs` |
| `PropositionInfo` 未知字段读写（反射链，已核对 0.1.18 成员名） | `Core/UnknownFields.cs` |
| 房主侧记录 + 下发（`Scan` / `Broadcast` 补字段 4、6） | `Features/Host/RecordDetail/` |
| 客户端拼回 `{what}` + 46 条词条注入（5 语言） | `Features/Client/ClueRender/` |
| 按房间推断活动（房主未装时的主力；歧义 → 「操作了设备」） | `Core/RoomDeviceScan.cs` |
| 死者 / 旁观者自动全线索（逐帧补扫 + 房主放行旁观者） | `Features/Client/FullClue/`、`Features/Host/SpectatorScan/` |
| 大厅「详细设置」开关 | `Features/UI/LobbyToggle/` |
| 伪证「详细活动」下拉 | `Features/Client/LieWhat/` |

## 5.1 验收步骤（需要实机）

1. 房主 + 至少一个客户端都装本插件 → 进对局 → 黑方操作一台设备（如终端机）→
   进入调查阶段扫描那台设备 → 线索应显示成 `…位于教室使用了终端机。`
2. 只有客户端装、房主不装 → 同样流程 → 线索应显示 `…位于教室使用了终端机。`
   （走客户端按房间推断）；若该房间有多种活动 → 显示 `…操作了设备。`
3. 房主在大厅「详细设置 → 其它」关掉「线索详细活动」→ 客户端那一行也应消失（跟随关闭）。
4. 非房主打开同一页面 → 那一行半透明、点不动。
5. 伪证页：点地图选一个房间 → 旁边应出现「详细活动」下拉，选一项后提交 → 审判时其他人
   看到的命题应带该活动。
6. **回归**：找一个没装插件的客户端进同一房间 → 它的线索显示必须与 0.1.18 原版完全一致
   （即 `…位于教室。`），且日志无异常。

计划中（见 `AGENTS.md` 的待办）：客户端能力握手、房主开关与大厅设置页单选框、
旧版伪证「详细活动」下拉的还原、死者/旁观者自动全线索与补扫设备。

## 6. 与 DT_Tools / HideAndSeek 的关系

独立插件：无编译期依赖、不含它们的代码或二进制，单独安装即可工作。

---

## 免责

本插件用于单人/好友自定义房间的玩法调整。修改会影响线索所表达的信息量，
在公开对局中使用前请确认房主与参与者都知情。
---

## 功能一览（2026-10 现状）

| 段名 | 作用 |
| --- | --- |
| `ClueRecordDetail` | 把 0.1.18 删掉的「详细活动」还原回线索句子 |
| `ClueMergeWindow` | 线索合并窗口可配（0 = 不合并） |
| `ClueCard` | 线索卡片补回旧版的「活动卡面 + 整句文本」两格 |
| `ClueRender` | 自采集 / 死者与旁观者全线索 |
| `GameChat` | `/say` `/clue` `/who` `/help`（黑队 `/say 0`） |
| `ClueShare` | 调查阶段黑 ↔ 黑幕线索互通（1 = 开局线索，2 = 搜索线索） |
| `ShadowSabotage` | 隐蔽破坏点（身份 / 时钟污染） |
| `ClueShootReminder` | 回放拍摄提醒（0.1.21 已删除回放系统，功能空转） |
| `CorpseMap` / `FlowerCarpet` | 尸体箭头 / 尸体撒花 |
| `SecondVote` | 黑幕二次投票 |
| `Log` | 日志总开关 |

> 0.1.21 删除了整个回放时间线系统；回放相关代码用 `#if TIMELINE` 保留，
> 构建时加 `-p:HasTimeline=true` 可重新启用。
