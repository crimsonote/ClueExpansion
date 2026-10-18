using System;
using Protocol;

namespace ClueDetail.Core
{
    /// <summary>
    /// 「详细活动」的唯一解析入口。
    /// </summary>
    /// <remarks>
    /// 三级来源，可靠性递减：
    /// <list type="number">
    /// <item><b>服务端精确下发</b> —— 房主装了本模块时走这条（<see cref="UnknownFields"/>，字段号 100）。</item>
    /// <item><b>设备关联</b> —— 两条路都精确到设备：① <c>S_ADD_CLUE</c> 自带 <c>DeviceId</c>
    /// （经 <c>Server.Game.ClueMirror.Add</c> 记下）；② <c>S_SCAN_DEVICE</c> 不带设备号，
    /// 但那是本机刚发起的扫描，靠请求/响应配对得到。</item>
    /// <item><b>按房间推断</b> —— 房主没装时的主力：用线索的房间去设备表里找该房间有什么活动。
    /// 去重后只剩一种 → 确定；两种以上 → <c>ambiguous</c>，调用方退化为通用文案。</item>
    /// </list>
    /// </remarks>
    internal static class ClueWhat
    {
        public static EWhatType Resolve(ClueManager.Clue clue)
        {
            return TryResolve(clue, out EWhatType what, out _) ? what : EWhatType.WNone;
        }

        /// <summary>
        /// 解析详细活动。<paramref name="ambiguous"/> 为 true 表示"知道房间里有活动，但不止一种"，
        /// 此时 <paramref name="what"/> 无意义。
        /// </summary>
        public static bool TryResolve(ClueManager.Clue clue, out EWhatType what, out bool ambiguous)
        {
            what = EWhatType.WNone;
            ambiguous = false;

            if (clue == null || clue.Info == null)
            {
                return false;
            }

            // 房主装了但关掉了本模块 → 连客户端自采集也一起停，否则等于把房主关掉的信息又补回来。
            if (!RoomState.AllowDetail)
            {
                return false;
            }

            int fromPacket = UnknownFields.GetWhat(clue.Info);
            if (fromPacket != 0)
            {
                what = (EWhatType)fromPacket;
                return true;
            }

            EWhatType byDevice = ActivityMap.WhatOfDevice(
                ActivityMap.DeviceOf(clue.Info.PlayerId, clue.Info.Time, clue.Info.RoomId));
            if (byDevice != EWhatType.WNone)
            {
                what = byDevice;
                return true;
            }

            if (!SelfCapture.On)
            {
                return false;
            }

            var candidates = RoomDeviceScan.CandidatesOf(clue.Info.RoomId);
            if (candidates.Count == 0)
            {
                return false;
            }

            if (candidates.Count == 1)
            {
                what = candidates[0];
                return true;
            }

            ambiguous = true;
            return true;
        }

        /// <summary>
        /// 取词条文本。0.1.18 的语言表删掉了这些键，所以先确保已注入，再判断是否命中
        /// （命中不到时 <c>Managers.GetText</c> 会返回 key 本身，这里据它判失败）。
        /// </summary>
        public static string TextOf(EWhatType what)
        {
            if (what == EWhatType.WNone)
            {
                return null;
            }

            Localization.Ensure();

            string key = what.ToString();
            string text = Managers.GetText(key);
            if (string.IsNullOrEmpty(text) || text == key)
            {
                return null;
            }

            return text;
        }

        /// <summary>
        /// 把句子里 <c>{what}</c> 那一段**换成真实活动**（= 还原 0.1.17a 的行为）。
        /// </summary>
        /// <remarks>
        /// ⚠️ 2026-10-18 修正：本模块最早的实现是「在句末标点前**插入**一段活动」，
        /// 那是基于一个**错误的前提** —— 以为 0.1.18 把句子模板里的 <c>{what}</c> 删了。
        /// 实际读 0.1.18 的 <c>Util.FormatProposition</c>（:27343）：<c>{what}</c> 分支**还在**，
        /// 只是内容从 <c>clue.Info.WhatType.ToString()</c>（每台设备的具体活动，0.1.17a :27332）
        /// 降级成了 <c>GetRoleWhatKey(clue.Info.PlayerId)</c>（:27517）——
        /// 一个**角色级的两选一**：黑幕（11037）⇒ <c>WhatCutPower</c>，
        /// 其余所有人 ⇒ <c>WhatDestroyEvidence</c>。
        /// <para>
        /// 所以正确做法是**替换那一段**，而不是再插一段
        /// （后者会显示成「…在 科学室 销毁了证据 操作了电脑。」）。
        /// </para>
        /// <para>
        /// 定位方式刻意不依赖旧文本：0.1.18 在 <c>{what}</c> 处渲染的一定是
        /// <c>&lt;#FF97AB&gt;…&lt;/color&gt;</c>，而<b>整句里只会有这一处</b>这个颜色标记，
        /// 所以按颜色标记替换即可，不需要反射私有的 <c>GetRoleWhatKey</c>。
        /// </para>
        /// </remarks>
        public static bool TryDecorate(string sentence, ClueManager.Clue clue, out string result)
        {
            result = sentence;
            if (string.IsNullOrEmpty(sentence))
            {
                return false;
            }

            if (!TryResolve(clue, out EWhatType what, out bool ambiguous))
            {
                return false;
            }

            string text = ambiguous ? Localization.GenericActivity : TextOf(what);
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            System.Text.RegularExpressions.Match match = WhatSegment.Match(sentence);
            if (!match.Success)
            {
                return false;
            }

            result = sentence.Substring(0, match.Index)
                     + "<#FF97AB>" + text + "</color>"
                     + sentence.Substring(match.Index + match.Length);
            return true;
        }

        /// <summary>
        /// <c>{what}</c> 在渲染结果里的样子（0.1.18 的固定色号）。
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex WhatSegment =
            new System.Text.RegularExpressions.Regex("<#FF97AB>[^<]*</color>");
    }
}
