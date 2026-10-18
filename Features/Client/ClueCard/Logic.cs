using System.Collections.Generic;
using ClueDetail.Core;
using Data;
using Protocol;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ClueDetail.Features.Client.ClueCard
{
    /// <summary>
    /// 卡片实例上「我们补出来的那两格」的持有者（**每个卡片实例一个**）。
    /// </summary>
    /// <remarks>
    /// 为什么需要它：补丁是 <c>static</c> 的，而节点是**每个实例**各自的
    /// （同一个 <c>UI_ClueSubItem</c> 实例会按列表需要反复 <c>SetInfo</c> 复用，
    /// 甚至在线索行与伪证行之间来回切换）。
    /// <para>
    /// <see cref="Live"/> 只为**热关闭**服务：那时要把场上所有补出来的节点撤掉
    /// （见 <see cref="ClueCardBuilder.ResetAll"/>）。用 <c>Awake</c>/<c>OnDestroy</c>
    /// 维护而不是 <c>FindObjectsOfType</c>：后者默认找不到「未激活」的对象，
    /// 而平板关掉时这些卡片正好是未激活的。
    /// </para>
    /// </remarks>
    internal sealed class ClueCardExtra : MonoBehaviour
    {
        private static readonly List<ClueCardExtra> Live = new List<ClueCardExtra>();

        private static readonly GameObject[] None = new GameObject[0];

        /// <summary>补出来的节点是否已经就位。</summary>
        public bool Built;

        /// <summary>这一份节点是给「伪证行」建的还是给「线索行」建的（两级容器不同）。</summary>
        public bool IsStatement;

        /// <summary>鼠标此刻是否停在这一行上。</summary>
        public bool Hovering;

        /// <summary>补出来的活动卡面（克隆自房间卡）。</summary>
        public GameObject WhatImage;

        /// <summary>活动卡面的矩形（版式按它调）。</summary>
        public RectTransform WhatRect;

        /// <summary>补出来的整句文本框（克隆自房间名文本框）。</summary>
        public TMP_Text Sentence;

        /// <summary>整句文本框的矩形。</summary>
        public RectTransform SentenceRect;

        /// <summary>原版的图标组节点 —— 悬浮显示整句时要临时把它们藏起来。</summary>
        public GameObject[] IconNodes = None;

        /// <summary>图标组各节点在 prefab 里的原始激活状态（取消悬浮时按它恢复）。</summary>
        public bool[] IconActive;

        public RectTransform WhereImage;

        public RectTransform WhereText;

        public RectTransform WhenText;

        public RectTransform Line;

        private void Awake()
        {
            Live.Add(this);
        }

        private void OnDestroy()
        {
            Live.Remove(this);
        }

        /// <summary>取一份当前存活实例的快照（遍历期间可能有对象被销毁，所以先拷出来）。</summary>
        public static ClueCardExtra[] Snapshot()
        {
            return Live.ToArray();
        }
    }

    /// <summary>
    /// 线索卡片上「活动卡面 + 整句文本」两格的全部逻辑。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 0.1.18 的 <c>UI_ClueSubItem</c> prefab（<c>common_prefab</c> bundle，地址
    /// <c>Assets/@Resources/Prefabs/UI/SubItem/UI_ClueSubItem.prefab</c>）里，
    /// 线索行的图标区是 <c>Clue/ClueContent/ClueIconParent</c>，其中直接子节点为：
    /// <c>ClueIconPanel</c>（整条底图）/ <c>ClueWhereImage</c>（房间卡 150x80）/
    /// <c>ClueWhereText</c>（房间名 140x60）/ <c>ClueIconLine</c>（分隔竖线 4x80）/
    /// <c>ClueWhenText</c>（时间 140x60）。伪证行是同一套结构，容器叫
    /// <c>Statement/StatementFrame/StatementContent/StatementIconParent</c>。
    /// </para>
    /// <para>
    /// 我们**不重建 prefab**，只克隆现成节点：
    /// 活动卡面克隆 <c>ClueWhereImage</c>（同一条底图上的一格），
    /// 整句克隆 <c>ClueWhereText</c>（同款 <c>TMP_Text</c>，字体材质跟着来）。
    /// 克隆出来的图形一律关掉 <c>raycastTarget</c>：行悬停靠的是整条的
    /// <c>MousePanel</c>，多一个射线目标就可能把悬停事件截胡。
    /// </para>
    /// </remarks>
    internal static class ClueCardBuilder
    {
        /// <summary>补出来的活动卡面节点名（同时也是「这一格是我们加的」的标记）。</summary>
        public const string WhatNodeName = "ClueDetailWhatCard";

        /// <summary>补出来的整句文本框节点名。</summary>
        public const string SentenceNodeName = "ClueDetailSentence";

        /// <summary>整句文本框的高度（= 旧版详情句节点 ClueText 的 100，宽度默认同为 350）。</summary>
        private const float SentenceHeight = 100f;

        /// <summary>伪证行的整句高度（= 旧版 StatementText 的 70，宽度 355）。</summary>
        private const float StatementSentenceHeight = 70f;

        private static readonly HashSet<string> MissingSprites = new HashSet<string>();

        private static bool _warned;

        private static bool _childrenLogged;

        private static bool _guessedLogged;

        /// <summary>
        /// 按这一行实际是「线索」还是「伪证」补齐节点、套版式、填内容。
        /// </summary>
        public static void Apply(UI_ClueSubItem item, ClueManager.Clue clue)
        {
            if (item == null || clue == null)
            {
                return;
            }

            ClueCardExtra extra = item.GetComponent<ClueCardExtra>();
            if (extra == null)
            {
                extra = item.gameObject.AddComponent<ClueCardExtra>();
            }

            // 同一个实例会在线索行 / 伪证行之间复用 —— 两级的容器不同，切换时重建。
            if (extra.Built && extra.IsStatement != clue.IsStatement)
            {
                Demolish(extra);
            }

            if (!extra.Built && !Build(extra, item, clue.IsStatement))
            {
                return;
            }

            ApplyLayout(extra, clue.IsStatement);
            FillWhatCard(extra, clue);
            FillSentence(extra, clue);

            // 版式与内容都刷新完之后，按「鼠标此刻在不在这一行上」把显隐对齐一次。
            SetHover(extra, extra.Hovering);
        }

        /// <summary>线索被判为无效（原版走 <c>ClearInfo</c>）时，把我们那一格也清掉。</summary>
        public static void Clear(UI_ClueSubItem item)
        {
            if (item == null)
            {
                return;
            }

            ClueCardExtra extra = item.GetComponent<ClueCardExtra>();
            if (extra == null)
            {
                return;
            }

            SetImageEnabled(extra.WhatImage, false);
            if (extra.Sentence != null)
            {
                extra.Sentence.text = string.Empty;
            }

            SetHover(extra, false);
        }

        /// <summary>悬停切换（由原版 <c>OnEnterClue</c> / <c>OnExitClue</c> 的 Postfix 调）。</summary>
        public static void SetHover(UI_ClueSubItem item, bool hovering)
        {
            if (item == null)
            {
                return;
            }

            ClueCardExtra extra = item.GetComponent<ClueCardExtra>();
            if (extra == null || !extra.Built)
            {
                return;
            }

            SetHover(extra, hovering);
        }

        /// <summary>热关闭：把场上所有补出来的节点撤掉，并恢复被我们藏起来的原版节点。</summary>
        public static void ResetAll()
        {
            ClueCardExtra[] all = ClueCardExtra.Snapshot();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null)
                {
                    Demolish(all[i]);
                }
            }

            MissingSprites.Clear();
            _warned = false;
            _childrenLogged = false;
            _guessedLogged = false;
        }

        // ══════════════════ 建 / 拆 ══════════════════

        /// <summary>
        /// 克隆出两格节点。返回 false 表示这一份 prefab 的结构与预期不符 ——
        /// 此时**什么都不做**（原版观感保留），并且只记一次日志。
        /// </summary>
        private static bool Build(ClueCardExtra extra, UI_ClueSubItem item, bool isStatement)
        {
            string contentName = isStatement ? "StatementIconParent" : "ClueIconParent";
            string whereImageName = isStatement ? "StatementWhereImage" : "ClueWhereImage";
            string whereTextName = isStatement ? "StatementWhereText" : "ClueWhereText";
            string whenTextName = isStatement ? "StatementWhenText" : "ClueWhenText";
            string lineName = isStatement ? null : "ClueIconLine";

            Transform content = FindDeep(item.transform, contentName);
            if (content == null)
            {
                WarnOnce("卡片上找不到内容容器 " + contentName + "（0.1.18 是否又改了 prefab？）");
                return false;
            }

            Transform whereImage = FindDeep(content, whereImageName);
            Transform whereText = FindDeep(content, whereTextName);
            Transform whenText = FindDeep(content, whenTextName);
            Transform line = lineName == null ? null : FindDeep(content, lineName);
            if (whereImage == null || whereText == null || whenText == null)
            {
                WarnOnce(
                    "卡片结构与预期不符：缺 " + whereImageName + " / " + whereTextName + " / " + whenTextName +
                    "（0.1.18 是否又改了 prefab？）");
                return false;
            }

            extra.WhereImage = whereImage as RectTransform;
            extra.WhereText = whereText as RectTransform;
            extra.WhenText = whenText as RectTransform;
            extra.Line = line as RectTransform;

            GameObject what = UnityEngine.Object.Instantiate(whereImage.gameObject, content);
            what.name = WhatNodeName;
            DisableRaycast(what);
            SetImageEnabled(what, false);
            extra.WhatImage = what;
            extra.WhatRect = what.GetComponent<RectTransform>();

            GameObject sentence = UnityEngine.Object.Instantiate(whereText.gameObject, content);
            sentence.name = SentenceNodeName;
            DisableRaycast(sentence);
            extra.Sentence = sentence.GetComponent<TMP_Text>();
            extra.SentenceRect = sentence.GetComponent<RectTransform>();
            sentence.SetActive(false);

            // 图标组 = 内容容器的**全部**子节点（除了我们那个整句文本框）——刻意不写死节点名：
            // 官方以后再加一格也不用改代码，悬浮时也不会漏掉谁。
            // （实机反馈过「悬浮时那条分隔条还在」，根因就是当初只枚举了当时认识的那几个名字。）
            var icons = new List<GameObject>(content.childCount);
            var actives = new List<bool>(content.childCount);
            for (int i = 0; i < content.childCount; i++)
            {
                GameObject child = content.GetChild(i).gameObject;
                if (child == sentence)
                {
                    continue;
                }

                icons.Add(child);
                actives.Add(child.activeSelf);
            }

            extra.IconNodes = icons.ToArray();
            extra.IconActive = actives.ToArray();

            LogChildrenOnce(contentName, content);

            extra.IsStatement = isStatement;
            extra.Built = true;
            return true;
        }

        /// <summary>
        /// 把图标区的子节点名打一次日志。
        /// </summary>
        /// <remarks>
        /// 实机排查「某条线还在 / 某一格不见了」时，这一行能直接给出**运行期**的节点集合
        /// —— 静态 dump 出来的 prefab 与运行期不一致时（历史上真踩过：dump 到了孤儿 bundle），
        /// 这是最快的对照。
        /// </remarks>
        private static void LogChildrenOnce(string contentName, Transform content)
        {
            if (_childrenLogged)
            {
                return;
            }

            _childrenLogged = true;
            var names = new List<string>(content.childCount);
            for (int i = 0; i < content.childCount; i++)
            {
                GameObject child = content.GetChild(i).gameObject;
                names.Add(child.name + (child.activeSelf ? string.Empty : "(未激活)"));
            }

            global::ClueDetail.Core.Log.Info(
                contentName + " 的图标区节点：" + string.Join(" / ", names.ToArray()));
        }

        /// <summary>撤掉补出来的节点，并把原版图标组恢复显示。</summary>
        private static void Demolish(ClueCardExtra extra)
        {
            if (extra.WhatImage != null)
            {
                UnityEngine.Object.Destroy(extra.WhatImage);
            }

            if (extra.Sentence != null)
            {
                UnityEngine.Object.Destroy(extra.Sentence.gameObject);
            }

            if (extra.IconNodes != null)
            {
                for (int i = 0; i < extra.IconNodes.Length; i++)
                {
                    if (extra.IconNodes[i] != null)
                    {
                        bool wasActive = extra.IconActive == null || i >= extra.IconActive.Length || extra.IconActive[i];
                        extra.IconNodes[i].SetActive(wasActive);
                    }
                }
            }

            extra.WhatImage = null;
            extra.WhatRect = null;
            extra.Sentence = null;
            extra.SentenceRect = null;
            extra.IconNodes = new GameObject[0];
            extra.IconActive = null;
            extra.WhereImage = null;
            extra.WhereText = null;
            extra.WhenText = null;
            extra.Line = null;
            extra.Built = false;
            extra.Hovering = false;
        }

        // ══════════════════ 版式 ══════════════════

        private static void ApplyLayout(ClueCardExtra extra, bool isStatement)
        {
            float size = Size();

            if (!isStatement)
            {
                // 线索行：位置与宽度全部走配置 —— 这正是「官方缩减掉的那一栏宽度」的还原入口。
                SetX(extra.WhereImage, ClueCardFeature.RoomX.Value);
                SetWidth(extra.WhereImage, ClueCardFeature.RoomWidth.Value);
                SetX(extra.WhereText, ClueCardFeature.RoomX.Value);
                SetWidth(extra.WhereText, ClueCardFeature.RoomWidth.Value);
                SetX(extra.Line, ClueCardFeature.LineX.Value);
                SetX(extra.WhenText, ClueCardFeature.WhenX.Value);

                if (ClueCardFeature.ShowWhatCard.Value)
                {
                    SetBox(extra.WhatRect, ClueCardFeature.WhatX.Value, size, size);
                }

                SetBox(
                    extra.SentenceRect,
                    ClueCardFeature.SentenceX.Value,
                    ClueCardFeature.SentenceWidth.Value,
                    SentenceHeight);
                return;
            }

            // 伪证行没有配置项（它的版式和线索行不同轴）：活动卡面塞进「房间」与「时间」之间的空档，
            // 塞不下就把时间整体右移 —— 伪证行右侧没有「提出命题」按钮，让位是安全的。
            RectTransform where = extra.WhereImage;
            RectTransform when = extra.WhenText;
            if (where == null || when == null)
            {
                return;
            }

            float whereRight = where.anchoredPosition.x + where.sizeDelta.x * 0.5f;
            float whenLeft = when.anchoredPosition.x - when.sizeDelta.x * 0.5f;
            float need = size + Gap * 2f;
            if (whenLeft - whereRight < need)
            {
                float shift = need - (whenLeft - whereRight);
                when.anchoredPosition = new Vector2(when.anchoredPosition.x + shift, when.anchoredPosition.y);
                whenLeft += shift;
            }

            if (ClueCardFeature.ShowWhatCard.Value)
            {
                SetBox(extra.WhatRect, (whereRight + whenLeft) * 0.5f, size, size);
            }

            float whereLeft = where.anchoredPosition.x - where.sizeDelta.x * 0.5f;
            float whenRight = when.anchoredPosition.x + when.sizeDelta.x * 0.5f;
            float center = (whereLeft + whenRight) * 0.5f;
            float width = Mathf.Clamp(whenRight - whereLeft, 200f, 596f);
            SetBox(extra.SentenceRect, center, width, StatementSentenceHeight);
        }

        /// <summary>活动卡面之间的最小留白。</summary>
        private const float Gap = 8f;

        private static float Size()
        {
            return Mathf.Clamp(ClueCardFeature.WhatCardSize.Value, 16f, 200f);
        }

        // ══════════════════ 内容 ══════════════════

        /// <summary>
        /// 填活动卡面。图源沿用旧版口径：先 <c>{活动}_{房间}_card</c>，再 <c>{活动}_card</c>。
        /// </summary>
        /// <remarks>
        /// ⚠️ 活动解析不出来（<c>WNone</c>）时，按 <see cref="ClueCardFeature.GuessWhatCard"/> 决定：
        /// 默认退化为「该房间存在的第一种设备」的卡面（别让那一格空着），
        /// 但**文本不跟着猜** —— 仍是通用文案或原版句式。图片确实取不到时留空。
        /// </remarks>
        private static void FillWhatCard(ClueCardExtra extra, ClueManager.Clue clue)
        {
            if (extra.WhatImage == null || !ClueCardFeature.ShowWhatCard.Value)
            {
                SetImageEnabled(extra.WhatImage, false);
                return;
            }

            EWhatType what = ClueWhat.Resolve(clue);
            if (what == EWhatType.WNone)
            {
                what = GuessFromRoom(clue.Info.RoomId);
            }

            if (what == EWhatType.WNone)
            {
                SetImageEnabled(extra.WhatImage, false);
                return;
            }

            Sprite sprite = LoadCard(what.ToString(), RoomTypeName(clue.Info.RoomId));
            if (sprite == null)
            {
                SetImageEnabled(extra.WhatImage, false);
                return;
            }

            Image image = extra.WhatImage.GetComponent<Image>();
            if (image == null)
            {
                return;
            }

            image.sprite = sprite;
            image.color = Color.white;
            image.enabled = true;
        }

        /// <summary>
        /// 活动解析不出来时的兜底：这个房间里第一种设备。
        /// </summary>
        /// <remarks>
        /// 为什么敢给：候选集是从**客户端自己的设备表**里按房间反查出来的
        /// （<see cref="RoomDeviceScan.CandidatesOf"/>），所以图上这台设备**确实在那个房间里**，
        /// 只是不一定就是这条线索对应的那一台。房主明确关了下发时（<c>AllowDetail == false</c>）
        /// 一律不给 —— 那是"别补"，不是"补不出来"。
        /// </remarks>
        private static EWhatType GuessFromRoom(int roomId)
        {
            if (ClueCardFeature.GuessWhatCard == null || !ClueCardFeature.GuessWhatCard.Value)
            {
                return EWhatType.WNone;
            }

            if (!RoomState.AllowDetail)
            {
                return EWhatType.WNone;
            }

            List<EWhatType> candidates = RoomDeviceScan.CandidatesOf(roomId);
            if (candidates == null || candidates.Count == 0)
            {
                return EWhatType.WNone;
            }

            if (!_guessedLogged)
            {
                _guessedLogged = true;
                global::ClueDetail.Core.Log.Info("线索卡片：有线索解析不出具体活动，卡面退化为该房间的第一种设备。");
            }

            return candidates[0];
        }

        private static void FillSentence(ClueCardExtra extra, ClueManager.Clue clue)
        {
            if (extra.Sentence == null)
            {
                return;
            }

            extra.Sentence.text = Util.FormatProposition(clue) ?? string.Empty;

            if (ClueCardFeature.SentenceFontSize != null && ClueCardFeature.SentenceFontSize.Value > 0f)
            {
                // 照旧版 ClueText 节点（350x100 @ -25.3）的 TMP 设置：m_fontSize=16、
                // m_enableAutoSizing=1（min 5 / max 16）、m_lineSpacing=45、
                // m_HorizontalAlignment=1（Left —— 就是 SentenceAlignment 的默认值）。
                // ⚠️ 不这么做就是沿用模板（房间名文本框）的字号：那个是 35、上限也是 35，
                // 实机反馈的「悬浮字太大」正是它。
                float size = ClueCardFeature.SentenceFontSize.Value;
                extra.Sentence.enableAutoSizing = true;
                extra.Sentence.fontSizeMin = Mathf.Min(5f, size);
                extra.Sentence.fontSizeMax = size;
                extra.Sentence.fontSize = size;
                extra.Sentence.lineSpacing = 45f;
            }

            extra.Sentence.alignment = ParseAlignment(ClueCardFeature.SentenceAlignment?.Value);
        }

        private static Sprite LoadCard(string what, string room)
        {
            if (!string.IsNullOrEmpty(room))
            {
                Sprite byRoom = Load(what + "_" + room + "_card.sprite");
                if (byRoom != null)
                {
                    return byRoom;
                }
            }

            return Load(what + "_card.sprite");
        }

        private static Sprite Load(string key)
        {
            Sprite sprite = Managers.Resource.Load<Sprite>(key);

            // 预载是按 Addressables 标签整包做的（School / Lobby / Tutorial），
            // 某个键不在预载集合里就会静默拿到 null —— 记一次，便于判断是"资源没了"还是"没预载"。
            if (sprite == null && MissingSprites.Add(key))
            {
                global::ClueDetail.Core.Log.Warn("活动卡面 " + key + " 没能加载（未预载或资源已删），这一格留空。");
            }

            return sprite;
        }

        private static string RoomTypeName(int roomId)
        {
            Dictionary<int, RoomData> rooms = Managers.Data?.RoomDic;
            RoomData data;
            if (rooms != null && rooms.TryGetValue(roomId, out data) && data != null)
            {
                return global::System.Enum.GetName(typeof(ERoomType), data.Type);
            }

            return null;
        }

        // ══════════════════ 悬浮 ══════════════════

        private static void SetHover(ClueCardExtra extra, bool hovering)
        {
            extra.Hovering = hovering;

            bool showSentence =
                hovering &&
                ClueCardFeature.ShowSentence != null &&
                ClueCardFeature.ShowSentence.Value &&
                extra.Sentence != null &&
                !string.IsNullOrEmpty(extra.Sentence.text);

            for (int i = 0; i < extra.IconNodes.Length; i++)
            {
                if (extra.IconNodes[i] != null)
                {
                    bool wasActive = extra.IconActive == null || i >= extra.IconActive.Length || extra.IconActive[i];
                    extra.IconNodes[i].SetActive(wasActive && !showSentence);
                }
            }

            if (extra.Sentence != null)
            {
                extra.Sentence.gameObject.SetActive(showSentence);
            }
        }

        // ══════════════════ 小工具 ══════════════════

        private static void SetX(RectTransform rt, float x)
        {
            if (rt == null)
            {
                return;
            }

            Vector2 pos = rt.anchoredPosition;
            rt.anchoredPosition = new Vector2(x, pos.y);
        }

        private static void SetWidth(RectTransform rt, float width)
        {
            if (rt == null)
            {
                return;
            }

            Vector2 size = rt.sizeDelta;
            rt.sizeDelta = new Vector2(width, size.y);
        }

        private static void SetBox(RectTransform rt, float x, float width, float height)
        {
            if (rt == null)
            {
                return;
            }

            // 锚点固定成中心：这样 anchoredPosition 就是「相对整行中心」的偏移，
            // 配置项的含义不会被 prefab 里的锚点写法左右。
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(x, 0f);
            rt.sizeDelta = new Vector2(width, height);
        }

        private static void SetImageEnabled(GameObject go, bool enabled)
        {
            if (go == null)
            {
                return;
            }

            Image image = go.GetComponent<Image>();
            if (image != null)
            {
                image.enabled = enabled;
            }
        }

        private static void DisableRaycast(GameObject go)
        {
            if (go == null)
            {
                return;
            }

            Graphic[] graphics = go.GetComponentsInChildren<Graphic>(true);
            for (int i = 0; i < graphics.Length; i++)
            {
                graphics[i].raycastTarget = false;
            }
        }

        private static TextAlignmentOptions ParseAlignment(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return TextAlignmentOptions.Left;
            }

            switch (value.Trim().ToLowerInvariant())
            {
                case "center":
                case "centre":
                case "middle":
                    return TextAlignmentOptions.Center;
                case "right":
                    return TextAlignmentOptions.Right;
                default:
                    return TextAlignmentOptions.Left;
            }
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
            if (_warned)
            {
                return;
            }

            _warned = true;
            global::ClueDetail.Core.Log.Warn("线索卡片未能补版式：" + message + "（原版观感保留，不影响其它功能）。");
        }
    }
}
