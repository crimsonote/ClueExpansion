# ClueDetail · 线索详情

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

已完成（骨架 + 核心链路）：

- 补丁框架（`[PatchFeature]` + 段开关自动绑定）
- `PropositionInfo` 未知字段读写（反射链，已核对 0.1.18 的成员名）
- 旧版 46 条 `W*` 词条注入（5 语言，从 0.1.16b 基线生成，见 `Core/LocalizationData.cs`）
- 房主侧：`RecordLastUsingPlayer` 记录 → `Scan` / `Broadcast` 补发
- 客户端侧：`Util.FormatProposition` 拼回 `{what}`；扫描时自采集设备关联

计划中（见 `AGENTS.md` 的待办）：客户端能力握手、房主开关与大厅设置页单选框、
旧版伪证「详细活动」下拉的还原、死者/旁观者自动全线索与补扫设备。

## 6. 与 DT_Tools / HideAndSeek 的关系

独立插件：无编译期依赖、不含它们的代码或二进制，单独安装即可工作。

---

## 免责

本插件用于单人/好友自定义房间的玩法调整。修改会影响线索所表达的信息量，
在公开对局中使用前请确认房主与参与者都知情。
