using ClueDetail.Core;
using ClueDetail.Features.Host.RecordDetail;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ClueDetail.Features.UI.LobbyToggle
{
    /// <summary>
    /// 克隆原版开关行 → 换标题 → 重绑控件。
    /// </summary>
    /// <remarks>
    /// 三条铁律（来自 HideAndSeek 的 UI 框架实测，违反会静默出错）：
    /// <list type="number">
    /// <item><b>克隆出来的控件必须 <c>RemoveAllListeners()</c></b> ——
    /// <c>Instantiate</c> 会把原版监听器一起复制，症状是"拨我的开关连带改了原版设置"。</item>
    /// <item>写配置后必须显式 <c>Save()</c>，否则"改完退出就丢"。</item>
    /// <item>层级按**名字递归找**，不写死完整路径 —— 0.1.18 动过 prefab 层级。</item>
    /// </list>
    /// </remarks>
    internal static class LobbyToggle
    {
        /// <summary>「其它」页的内容容器（行都挂在它下面）。</summary>
        private const string ContentName = "ETCPresetContent";

        /// <summary>被克隆的原版开关行。它只是个"长得对"的模板，不参与功能。</summary>
        private const string TemplateRowName = "MastermindVoteLoss";

        private const string RowName = "ClueDetailBroadcastRow";

        /// <summary>这一行是否已经建出来了（自检用）。</summary>
        public static bool IsBuilt => _toggle != null;

        private static Toggle _toggle;
        private static CanvasGroup _group;

        /// <summary>克隆出来的整行（热关闭时要整行撤掉，不能只撤 Toggle 所在的子对象）。</summary>
        private static Transform _row;

        /// <summary>那一行的标题文本（改配置时要热更新它）。</summary>
        private static TMP_Text _title;

        private static bool _failedLogged;

        public static void Ensure(UI_LobbyPreset preset)
        {
            if (preset == null || _toggle != null)
            {
                return;
            }

            // 段被关掉时配置项不存在 —— 那就没有可绑的东西，安静退出。
            var entry = RecordDetailFeature.BroadcastDetail;
            if (entry == null)
            {
                return;
            }

            Transform content = FindDeep(preset.transform, ContentName);
            if (content == null)
            {
                WarnOnce("找不到设置页内容容器 " + ContentName);
                return;
            }

            Transform template = content.Find(TemplateRowName);
            if (template == null)
            {
                WarnOnce("找不到开关行模板 " + TemplateRowName);
                return;
            }

            if (content.Find(RowName) != null)
            {
                return;
            }

            Transform row = Object.Instantiate(template, content);
            row.name = RowName;
            _row = row;

            _title = FindTitle(row);
            ApplyTitle();

            _toggle = row.GetComponentInChildren<Toggle>(true);
            if (_toggle == null)
            {
                WarnOnce("克隆出来的行里没有 Toggle");
                Object.Destroy(row.gameObject);
                return;
            }

            _toggle.onValueChanged.RemoveAllListeners();
            _toggle.onValueChanged.AddListener(OnChanged);

            _group = _toggle.GetComponent<CanvasGroup>();
            if (_group == null)
            {
                _group = _toggle.gameObject.AddComponent<CanvasGroup>();
            }

            global::ClueDetail.Core.Log.Info("设置页已加入「线索详细活动」开关。");
        }

        /// <summary>
        /// 撤掉克隆出来的整行（功能被热关闭时）。
        /// </summary>
        /// <remarks>
        /// 不撤的后果：设置页里那一行还在，但它的回调已经没有任何代码在处理
        /// —— 房主拨它不会有任何效果，反而会让人以为设置坏了。
        /// </remarks>
        public static void Destroy()
        {
            if (_row != null)
            {
                Object.Destroy(_row.gameObject);
            }

            _row = null;
            _toggle = null;
            _group = null;
            _title = null;
            _failedLogged = false;
        }

        /// <summary>
        /// 把标题文本对齐到配置（**热更新**：改 RowTitle 立刻生效）。
        /// </summary>
        /// <remarks>
        /// 放在这里而不是订阅 <c>SettingChanged</c>：那个回调可能在 HTTP 线程触发
        /// （DT_Tools 的 CONFIG 页），直接改 <c>TMP_Text</c> 不安全。
        /// Sync 由原版设置页每次刷新时调用，本来就在主线程。
        /// </remarks>
        public static void ApplyTitle()
        {
            if (_title == null)
            {
                return;
            }

            string want = LobbyToggleFeature.RowTitle?.Value ?? "线索详细活动";
            if (_title.text != want)
            {
                _title.text = want;
            }
        }

        public static void Sync(UI_LobbyPreset preset)
        {
            Ensure(preset);
            if (_toggle == null)
            {
                return;
            }

            var entry = RecordDetailFeature.BroadcastDetail;
            if (entry == null)
            {
                return;
            }

            _toggle.SetIsOnWithoutNotify(entry.Value);

            ApplyTitle();

            bool isHost = RoomState.IsHost;
            _toggle.interactable = isHost;
            if (_group != null)
            {
                _group.alpha = isHost ? 1f : 0.4f;
            }
        }

        private static void OnChanged(bool value)
        {
            var entry = RecordDetailFeature.BroadcastDetail;
            if (entry == null)
            {
                return;
            }

            entry.Value = value;
            RoomState.HostBroadcast = value;

            // 与 HideAndSeek 的 UI 框架同一口径：改完立刻落盘，否则退出就丢。
            entry.ConfigFile?.Save();

            global::ClueDetail.Core.Log.Info($"线索详细活动已{(value ? "开启" : "关闭")}（本局即时生效，并会同步给装了本模块的客户端）。");
        }

        /// <summary>标题在 <c>TitleBg</c> 下；不能取"第一个 TMP_Text"（同行还有数值文本）。</summary>
        private static TMP_Text FindTitle(Transform row)
        {
            Transform bg = row.Find("TitleBg");
            if (bg != null)
            {
                TMP_Text text = bg.GetComponentInChildren<TMP_Text>(true);
                if (text != null)
                {
                    return text;
                }
            }

            return null;
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
            global::ClueDetail.Core.Log.Warn("设置页开关未能建立：" + message + "（不影响游戏本体，也不影响线索还原本身）。");
        }
    }
}
