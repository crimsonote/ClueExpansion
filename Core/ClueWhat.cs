using System;
using Protocol;

namespace ClueDetail.Core
{
    /// <summary>
    /// 「详细活动」的唯一解析入口：协议未知字段优先，其次客户端自采集的设备关联。
    /// </summary>
    internal static class ClueWhat
    {
        /// <summary>句末标点候选（用于把 {what} 插回句子尾部）。</summary>
        private static readonly char[] EndPunctuation = { '。', '.', '！', '!', '？', '?' };

        public static EWhatType Resolve(ClueManager.Clue clue)
        {
            if (clue == null || clue.Info == null)
            {
                return EWhatType.WNone;
            }

            int fromPacket = UnknownFields.GetWhat(clue.Info);
            if (fromPacket != 0)
            {
                return (EWhatType)fromPacket;
            }

            int deviceId = ActivityMap.DeviceOf(clue.Info.PlayerId, clue.Info.Time, clue.Info.RoomId);
            return ActivityMap.WhatOfDevice(deviceId);
        }

        /// <summary>
        /// 取词条文本。0.1.18 的语言表删掉了这些键，所以先确保已注入，再判断是否命中。
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
        /// 而 0.1.18 的模板已经把 {what} 整段删掉，只能按标点回插。
        /// 找不到句末标点时直接追加。
        /// </remarks>
        public static bool TryDecorate(string sentence, ClueManager.Clue clue, out string result)
        {
            result = sentence;
            if (string.IsNullOrEmpty(sentence))
            {
                return false;
            }

            string what = TextOf(Resolve(clue));
            if (what == null)
            {
                return false;
            }

            string decorated = "<#FF97AB>" + what + "</color>";
            int index = sentence.LastIndexOfAny(EndPunctuation);
            result = index >= 0 ? sentence.Insert(index, decorated) : sentence + decorated;
            return true;
        }
    }
}
