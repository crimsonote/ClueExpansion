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
    /// <item><b>服务端精确下发</b> —— 房主装了本模块时走这条（<see cref="UnknownFields"/> 字段号 4）。</item>
    /// <item><b>扫描建立的设备关联</b> —— 本机扫过那台设备，能把线索精确对到设备上。</item>
    /// <item><b>按房间推断</b> —— 房主没装时的主力：用线索的房间去设备表里找该房间有什么活动。
    /// 去重后只剩一种 → 确定；两种以上 → <c>ambiguous</c>，调用方退化为通用文案。</item>
    /// </list>
    /// </remarks>
    internal static class ClueWhat
    {
        /// <summary>句末标点候选（用于把 {what} 插回句子尾部）。</summary>
        private static readonly char[] EndPunctuation = { '。', '.', '！', '!', '？', '?' };

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
        /// 把详细活动拼回句子。位置：句末标点之前（与旧版 "…于{where}{what}。" 一致）。
        /// </summary>
        /// <remarks>
        /// 这是"尽量像旧版"的降级实现：旧版模板把 {what} 放在 {where} 之后，
        /// 而 0.1.18 的模板已经把 {what} 整段删掉，只能按标点回插。找不到句末标点时直接追加。
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

            string decorated = "<#FF97AB>" + text + "</color>";
            int index = sentence.LastIndexOfAny(EndPunctuation);
            result = index >= 0 ? sentence.Insert(index, decorated) : sentence + decorated;
            return true;
        }
    }
}
