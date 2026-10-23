# 资源与 prefab：bundle 的坑

## 12. 资源与 prefab：bundle 的坑（2026-10-18）

⚠️ **同一个资源地址可能在多个 bundle 里各有一份，只有 `catalog.bin` 指向的那份会被加载。**

实测：`Assets/@Resources/Prefabs/UI/SubItem/UI_ClueSubItem.prefab` 在**整个安装的 45 个 bundle 里只有 2 份**
（2026-10-19 全量扫描）：
- `common_prefab_assets_all_4253c60d4b003cb4ebd36ee3d5cf745a.bundle`（0.1.18 的，**catalog 指向它**）
- `ui_assets_all_e5028a3ceeca10ef8bbe2f46f263871c.bundle`（**孤儿**：文件还在、地址表里也自称有它，
  但 `catalog.bin` 里搜不到它的哈希）

⚠️ **"孤儿 = 上一版（0.1.17a）的 prefab"这个推断是错的（2026-10-19 更正）。**
那份孤儿的 GameObject 名表里**根本没有** `ClueWhatImage` / `ClueWhereImage` / `ClueWhereText` /
`ClueWhenText` / `ClueIconParent`，只有 `ClueContent` / `ClueFrame` / `ClueBG` / `ClueWho*` / 两个 `ClueText`
—— 它是个**更早或别的变体**，不能拿来推断旧版版式。（上一轮我正是拿它当 0.1.17a，得出
「旧版就是头像 + 整句的朴素版」这个**错误结论**，并被用户当场纠正。）

**判版本的三条硬证据（按可信度排序）**：
1. **代码引用的节点名**（最硬）——某一版的 `UI_ClueSubItem` 会 `FindChild` 哪些名字，那版 prefab 就必须有那些节点；
2. `catalog.bin` 指向哪个 bundle（不在 = 孤儿）；
3. bundle 的**完整 GameObject 名表**（不是某棵子树的 dump）。

⚠️ **"活动卡面"到底属于哪个类（2026-10-19 第三次更正 —— 这次拿到了 0.1.17a 的真 prefab）**：
**两个类都有** `ClueWhatImage`：`UI_ClueSubItem`（平板线索行）与 `UI_ClueAddSubItem`（扫描仪线索卡）。

| 版本 | `UI_ClueSubItem`（平板线索行） | `UI_ClueAddSubItem`（扫描仪卡） |
| --- | --- | --- |
| 0.1.16b | GameObjects 含 `ClueIconParent`/`ClueTextParent`/…；Texts 含 `ClueText`/`ClueWhereText`/`ClueWhenText`；Images 含 `ClueWhereImage`+**`ClueWhatImage`**（:84459） | Images 含 **`ClueWhatImage`**（:84283） |
| 0.1.17a | **与 0.1.16b 一字不差**（:86080） | 同 0.1.16b（:85904） |
| 0.1.18 | GameObjects 只剩 5 项（两对容器全删）；Texts **删 `ClueText`**；Images **删 `ClueWhatImage`/`StatementWhatImage`**（:86425） | Images **删 `ClueWhatImage`**（:86263） |

⇒ **旧版平板线索行 = 图标视图（房间卡 + 活动卡面 + 房间名 + 时间）+ 整句视图（`ClueText`）的两视图行**，
0.1.18 才把活动卡面与整句删掉 —— 这正是本模块存在的理由。

### 0.1.17a 的**真实** `UI_ClueSubItem`（从 Steam 旧 manifest 下到的原包读出，不是反推）

```text
UI_ClueSubItem 612x104                        0.1.18 对照
  MousePanel
  Clue
    ClueWho        @(-254, 8) 100x100         ← 与 0.1.18 相同
      ClueWhoFrame / ClueWhoMask / ClueWhoImage(196x191 @0,-19.1) / ClueWhoText(70x20 @ y=-38)
    ClueContent
      ClueTextParent  100x100                 ← 0.1.18 删
        ClueTextPanel @(0,0)     596x88       ← 0.1.18 删
        ClueText      @(-10,0)   380x100      ← 整句视图（字号 16 / min5 / max16 / 行距 45 / 左对齐）；0.1.18 删
      ClueIconParent  100x100
        ClueIconPanel @(0,0)     596x88       ← 相同
        ClueWhereImage@(-135,0)  150x80       ← 相同
        ClueWhereText @(-135,0)  140x60       ← 相同
        ClueIconLine  @(-54,0)   4x80         ← ⚠ 旧版就有，0.1.18 也是 -54（**不是 0.1.18 新加**）
        ClueWhatImage @(-8,0)    80x80        ← ★ 活动卡面；0.1.18 删
        ClueWhenText  @(113,0)   140x60       ← 0.1.18 改成 @(71,0)
    PropositionButton @(244,0) 100x80         ← 与 0.1.18 相同（SubmitBlockImage 35x35 / SubmitText 80x50）
  Statement（同构的伪证行）：StatementIconParent{WhereImage @-4.5, WhatImage @114.5 80x80,
                              WhereText @-4.5, WhenText @226.5} + StatementTextParent{StatementText @-20 355x70}
  Selected
```

**以上真值已全部落成 `[ClueCard]` 的默认值**：`RoomX=-135` / `WhatX=-8` / `WhatCardSize=80` /
`ShowLine=true` / `LineX=-54` / `WhenX=113` / `SentenceX=-10` / `SentenceWidth=380` /
`SubmitX=244` / `SubmitWidth=100` / `SubmitHeight=80`（老 cfg 由 `MigrateLegacyDefaults` 一次性搬运）。

### 怎么再拿到旧版资源（唯一可靠的正路）

安装目录 45 个 bundle 里 **38 个还是 0.1.17a 的旧文件**，但线索行旧 prefab 恰好落在
**被 0.1.18 替换的那 7 个**里（`common_prefab` / `common_material` / `language` / `school_prefab` /
`common_sprite` / `common_sound` / monoscripts）⇒ 只能从 Steam 拉旧 manifest：

```text
Win+R → steam://open/console → 粘贴回车：
download_depot 3088400 3088401 7164276621005135491 6072389213422104303
```
- AppID `3088400`；内容 depot `3088401`（Steam API 查得，public 分支）
- `7164276621005135491` = **2026-10-08 05:10:59 UTC**（0.1.17a）的 manifest；
  `6072389213422104303` = 当前版，作为 **delta** ⇒ 只下差异（实测 **258.5 MB / 22 个文件**）
- 落到 `Steam\steamapps\content\app_3088400\depot_3088401\`，**不动已装游戏**
- 本机备份：`.tmps\depot-0.1.17a\`；关键包 `common_prefab_assets_all_fd4e4345…bundle`，
  SHA256 `5751C8671E3628E26E06512E23AA61F774A47BE906D3D3437E37D1A70CE890CC`
- ⚠️ `steamcmd` 独立跑不通（新版 Steam 令牌机器加密，报 `Cached credentials not found`）；
  后台进程也抢不到前台 ⇒ 这一步只能人工在 Steam 控制台敲。

⚠️ **别信安装目录里那份 `ui_assets` 的 `UI_ClueSubItem`**：它只有
`ClueWho` / `ClueText`(350x100 @-25.3) / `PropositionButton`(136x64 @215)，
**比 0.1.16b 还老**（Steam 没改写这个文件 ⇒ 时间戳 10-08 但内容是更早的残留）。
我一度拿它当"0.1.17a 平板行"，据此改错过一版默认值（含误判"黄竖线是 0.1.18 新加"）。

**顺手记两条工具口径**：
- 读 prefab 层级**别走 `GameObject.m_Component`**（UnityPy 1.25 下解不出 PPtr，会得到"子 0"的假树）；
  用 **`RectTransform.m_Father` + `m_GameObject`** 反查父子链，稳。脚本：`.tmps\tools\_tree3.py`。
- `probe-addressables.py` 只打印"名字含关键词"的对象 ⇒ **关键词会漏别名**，判版本要先看全量名表。

**方法论教训（实际踩到的两条）**：
- **关键词过滤会漏**：`probe-addressables.py` 只打印"名字里含关键词"的对象 —— 我第一次用
  `cluesubitem lie`，`ClueWhatImage`（不含这两个词）就被整片滤掉。⇒ 关键词要覆盖**别名与兄弟节点名**，
  或者干脆打印**全量名字表**。
- **自写遍历脚本必须先跑对照组**：我给 `UI_ClueSubItem` 写的 `m_Component` 遍历在 UnityPy 1.25 下
  两个包都返回"子 0"（API 变了）。若当时拿已知的新版树做对照，当场就能发现脚本坏了。

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
