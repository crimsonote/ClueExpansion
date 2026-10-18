using System.Collections.Generic;
using ClueDetail.Core;
using Data;
using Protocol;
using TMPro;
using UnityEngine;

namespace ClueDetail.Features.Client.LieWhat
{
    /// <summary>
    /// 伪证页「活动」那一行的建立与刷新。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 0.1.18 把这一行**整行删掉**了：`GameObjects` 枚举里 `WhatDrop`（0.1.17a 第 67 号）没了、
    /// `Texts` 里 `LieWhatText` 没了、`RefreshWhat` / `OnWhatDropdownValueChanged` 整组没了。
    /// 旧版 prefab 的结构（从上一版残留的 bundle 里读到）是：
    /// <c>LieList</c> 下 <c>LieWhereText</c>(y=301.9) / <c>Who</c>(y=180) / <c>When</c>(y=50) /
    /// <c>What</c>(y=-80) / <c>LieClueParent</c>(y=-208)，其中 <c>What</c> 行内是
    /// <c>LieWhatText</c>(300x50 @ (-250,+50)) + <c>WhatDrop</c>(**550x60** @ (0,0))。
    /// 0.1.18 删掉 What 行后把 Who/When 上移了（y=140 / -40），<c>LieClueParent</c> 没动。
    /// </para>
    /// <para>
    /// 所以这里**克隆 <c>Who</c> 整行**（它同样是「行标题 + 一个下拉」的结构，行内相对位置与旧版
    /// <c>LieWhatText</c> 完全一致），改名后按 <c>When</c> 行下方固定间距放回去；
    /// 下方空间不够就把预览卡 <c>LieClueParent</c> 往下让，避免压住它。
    /// </para>
    /// <para>
    /// ⚠️ 两条铁律（都实际踩过或有硬依据，见 AGENTS.md §4.8）：
    /// ① <b>克隆出来立刻改名</b> —— 原版 <c>BindTexts</c>/<c>BindObjects</c> 是**按名字递归找**的
    /// （UI_Base.Bind :91010-91029），留着 `LieWhoText`/`WhoDrop` 这类名字，将来原版再绑一次
    /// 就可能绑到我们这份上；
    /// ② <b>必须 <c>RemoveAllListeners()</c></b> —— <c>Instantiate</c> 会把原版指向
    /// <c>UI_GameTablet.OnWhoDropdownValueChanged</c> 的监听器一起复制，
    /// 症状是「拨我们的下拉，连带改了原版的『谁』」。
    /// </para>
    /// </remarks>
    internal static class LieWhatRow
    {
        private const string RowName = "ClueDetailWhatRow";
        private const string LabelName = "ClueDetailWhatLabel";
        private const string DropName = "ClueDetailWhatDrop";

        /// <summary>可克隆的模板行（同样只有一个下拉，行内标题位置与旧版 What 行一致）。</summary>
        private const string TemplateRowName = "Who";

        /// <summary>旧版 What 行在 When 行下方 130px；0.1.18 把 When 上移了 90，所以这里也收一点。</summary>
        private const float RowOffsetBelowWhen = -110f;

        /// <summary>旧版 WhatDrop 就是 550x60（比 WhoDrop 的 286 宽得多，活动名长）。</summary>
        private const float RowWidth = 550f;

        /// <summary>下拉里那行文字；旧版 Label 是 200 宽，活动名长会被自动缩字号，这里放宽。</summary>
        private const float CaptionWidth = 500f;

        /// <summary>预览卡与本行的最小垂直间距（本行下拉底边到卡片顶边）。</summary>
        private const float PreviewGap = 8f;

        private static readonly List<EWhatType> Options = new List<EWhatType>();

        private static List<EWhatType> _allActivities;

        private static UI_GameTablet _tablet;
        private static TMP_Dropdown _dropdown;
        private static TMP_Text _label;
        private static bool _failedLogged;
        private static bool _fallbackLogged;

        /// <summary>那一行是否已经建出来了（自检用）。</summary>
        public static bool IsBuilt => _dropdown != null;

        /// <summary>
        /// 建出整行（幂等）。<paramref name="tablet"/> 换了实例会整行重建。
        /// </summary>
        public static void Build(UI_GameTablet tablet)
        {
            if (tablet == null)
            {
                return;
            }

            if (_dropdown != null && _tablet == tablet)
            {
                return;
            }

            // 平板换了实例（回大厅再来一局）时，旧的那份已经随旧实例销毁 —— 只清引用。
            Reset();

            Transform list = FindDeep(tablet.transform, "LieList");
            if (list == null)
            {
                WarnOnce("伪证页找不到容器 LieList");
                return;
            }

            Transform row = list.Find(RowName);
            if (row == null)
            {
                Transform template = list.Find(TemplateRowName);
                if (template == null)
                {
                    WarnOnce("伪证页找不到可克隆的模板行 " + TemplateRowName);
                    return;
                }

                row = Object.Instantiate(template, list);
                row.name = RowName;
                PlaceRow(list, row as RectTransform);
            }

            _tablet = tablet;

            if (!Adopt(row))
            {
                // 认领失败（结构不符）—— 把半成品撤掉，别留一行残缺的控件。
                if (row != null)
                {
                    Object.Destroy(row.gameObject);
                }

                _tablet = null;
                return;
            }

            // 先铺一次（此时还没选房间 ⇒ 走「全部活动」的兜底），别让下拉只有一个「-」。
            RefreshOptions(null);

            global::ClueDetail.Core.Log.Info("伪证界面已还原「活动」一栏（行标题 + 下拉）。");
        }

        /// <summary>玩家在地图上点了某个区域（选房间）后刷新选项。</summary>
        public static void Refresh(UI_GameTablet tablet, RoomData room)
        {
            if (_dropdown == null || _tablet != tablet)
            {
                Build(tablet);
                if (_dropdown == null)
                {
                    return;
                }
            }

            RefreshOptions(room);
        }

        /// <summary>
        /// 撤掉整行（功能被热关闭时）。
        /// </summary>
        /// <remarks>
        /// 不撤的后果：界面上那一行还在，但已经没有任何代码在处理它
        /// —— 玩家选了活动却什么都不会发生（AGENTS.md §11.3 的 OnDisabled 铁律）。
        /// </remarks>
        public static void Destroy()
        {
            GameObject target = _dropdown != null ? _dropdown.gameObject : null;
            if (_label != null && _label.transform != null)
            {
                Transform row = _label.transform;
                while (row.parent != null && row.name != RowName)
                {
                    row = row.parent;
                }

                target = row.gameObject;
            }

            if (target != null)
            {
                Object.Destroy(target);
            }

            Reset();
        }

        // ══════════════════ 内部 ══════════════════

        /// <summary>把克隆出来的那一行认领成我们的：改名、换标题、重绑下拉、定宽度。</summary>
        private static bool Adopt(Transform row)
        {
            if (row == null)
            {
                return false;
            }

            TMP_Dropdown dropdown = row.GetComponentInChildren<TMP_Dropdown>(true);
            if (dropdown == null)
            {
                WarnOnce("克隆出来的行里没有下拉控件");
                return false;
            }

            TMP_Text label = null;
            Transform labelNode = FindDeep(row, "LieWhoText") ?? FindDeep(row, LabelName);
            if (labelNode != null)
            {
                labelNode.gameObject.name = LabelName;
                label = labelNode.GetComponent<TMP_Text>();
                if (label != null)
                {
                    Localization.Ensure();
                    label.text = Localization.WhatLabel ?? "活动";
                }
            }

            dropdown.gameObject.name = DropName;

            RectTransform rect = dropdown.transform as RectTransform;
            if (rect != null)
            {
                Vector2 size = rect.sizeDelta;
                rect.sizeDelta = new Vector2(RowWidth, size.y);
            }

            if (dropdown.captionText != null && dropdown.captionText.rectTransform != null)
            {
                RectTransform caption = dropdown.captionText.rectTransform;
                Vector2 size = caption.sizeDelta;
                caption.sizeDelta = new Vector2(CaptionWidth, size.y);
            }

            // ⚠️ 克隆会把原版监听器（指向 UI_GameTablet.OnWhoDropdownValueChanged）一起带过来。
            dropdown.onValueChanged.RemoveAllListeners();
            dropdown.onValueChanged.AddListener(OnChanged);

            if (dropdown.options == null || dropdown.options.Count == 0)
            {
                dropdown.ClearOptions();
                dropdown.AddOptions(new List<string> { "-" });
            }

            _label = label;
            _dropdown = dropdown;
            Options.Clear();
            return true;
        }

        /// <summary>把行放到旧版的位置，并给下方的预览卡让位。</summary>
        private static void PlaceRow(Transform list, RectTransform row)
        {
            if (row == null)
            {
                return;
            }

            float y = -150f;
            RectTransform whenRect = list.Find("When") as RectTransform;
            if (whenRect != null)
            {
                y = whenRect.anchoredPosition.y + RowOffsetBelowWhen;
            }

            row.anchoredPosition = new Vector2(0f, y);

            RectTransform preview = list.Find("LieClueParent") as RectTransform;
            if (preview == null)
            {
                return;
            }

            // 本行下拉底边 = y - 30（下拉 60 高）；预览卡 100 高，顶边 = previewY + 50。
            float need = y - 30f - PreviewGap - 50f;
            if (preview.anchoredPosition.y > need)
            {
                preview.anchoredPosition = new Vector2(preview.anchoredPosition.x, need);
            }
        }

        private static void RefreshOptions(RoomData room)
        {
            if (_dropdown == null)
            {
                return;
            }

            Options.Clear();
            var labels = new List<string> { "-" };   // 第一项 = 不指定活动（旧版也是这样）
            Options.Add(EWhatType.WNone);

            int candidates = 0;
            if (room != null)
            {
                foreach (EWhatType what in RoomDeviceScan.CandidatesOf(room.DataId))
                {
                    int before = Options.Count;
                    Add(what, labels);
                    if (Options.Count > before)
                    {
                        candidates++;
                    }
                }
            }

            // ⚠️ 默认**不列**与该房间无关的活动：伪证页做的是"编一条说得通的线索"，
            // 让人在图书馆伪造"打开了黑洞"没有意义。0.1.17a 更狠：那种房间根本点不进来
            // （HasLieWhatOptions :53482 判 ClueManager.RoomObjectDict）；0.1.18 改成只看
            // 服务端下发的 LieRoomSet（CanLieInRoom :53713），于是"没有设备的房间"也能进来。
            // 房间准入仍按服务端口径，但**列表**必须只出现这个房间真有的设备 ——
            // 除非显式打开 AllowAnyActivity（给"纯粹想编一个"的玩法留口子）。
            if (candidates == 0 &&
                LieWhatFeature.AllowAnyActivity != null &&
                LieWhatFeature.AllowAnyActivity.Value)
            {
                foreach (EWhatType what in AllActivities())
                {
                    Add(what, labels);
                }

                if (!_fallbackLogged)
                {
                    _fallbackLogged = true;
                    global::ClueDetail.Core.Log.Info(
                        "伪证：这个房间没有可调查设备，按 AllowAnyActivity 列出全部活动。");
                }
            }

            _dropdown.ClearOptions();
            _dropdown.AddOptions(labels);

            // ⚠️ ClearOptions / AddOptions 都**不会**复位 m_Value，而下拉框显示的是
            // options[m_Value] —— 不复位的话，框上会显示上一次那份列表里的第 value 项
            //（实测就出现了一条与本行毫不相干的词条）。先把值拉回第 0 项，再刷新显示。
            _dropdown.value = 0;
            _dropdown.RefreshShownValue();

            // value 本来就是 0 时 setter 不触发回调，这里显式把字段落回「不指定」，
            // 否则换房间后上一条伪造里选的活动会留在字段上。
            Apply(0);

            if (room != null)
            {
                global::ClueDetail.Core.Log.Info(
                    $"伪证：房间 {room.DataId}（{room.Type}）可选活动 {candidates} 种。");
            }
        }

        private static void Add(EWhatType what, List<string> labels)
        {
            if (what == EWhatType.WNone || Options.Contains(what))
            {
                return;
            }

            string text = ClueWhat.TextOf(what);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            labels.Add(text);
            Options.Add(what);
        }

        /// <summary>全部**有词条**的活动（兜底列表，只在房间没有任何设备时才用）。</summary>
        private static List<EWhatType> AllActivities()
        {
            if (_allActivities != null)
            {
                return _allActivities;
            }

            var list = new List<EWhatType>();
            global::System.Array values = global::System.Enum.GetValues(typeof(EWhatType));
            for (int i = 0; i < values.Length; i++)
            {
                var what = (EWhatType)values.GetValue(i);
                if (what != EWhatType.WNone && !string.IsNullOrEmpty(ClueWhat.TextOf(what)))
                {
                    list.Add(what);
                }
            }

            _allActivities = list;
            return list;
        }

        private static void OnChanged(int index)
        {
            Apply(index);
        }

        private static void Apply(int index)
        {
            if (index < 0 || index >= Options.Count)
            {
                return;
            }

            // 直接写进伪证线索的未知字段：提交时原版会 Clone.Info 发出去，
            // 而 Clone 会一并复制未知字段，所以服务端与其它装了本模块的客户端都收得到
            // （未装的客户端忽略未知字段，行为与原版一致）。
            PropositionInfo info = _tablet?.LieClue?.Clue?.Info;
            if (info == null)
            {
                return;
            }

            UnknownFields.ReplaceWhat(info, (int)Options[index]);
        }

        private static void Reset()
        {
            _tablet = null;
            _dropdown = null;
            _label = null;
            _failedLogged = false;
            _fallbackLogged = false;
            Options.Clear();
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null)
            {
                return null;
            }

            if (root.name == name)
            {
                return root;
            }

            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDeep(root.GetChild(i), name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static void WarnOnce(string message)
        {
            if (_failedLogged)
            {
                return;
            }

            _failedLogged = true;
            global::ClueDetail.Core.Log.Warn("伪证「活动」一栏未能建立：" + message + "（不影响其它功能）。");
        }
    }
}
