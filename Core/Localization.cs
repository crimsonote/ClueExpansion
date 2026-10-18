using System.Collections.Generic;
using Data;

namespace ClueDetail.Core
{
    /// <summary>
    /// 把 0.1.18 从语言表整体删除的设备活动词条（46 条）注入回 <c>Managers.Data.TextDic</c>，
    /// 外加本模块自有词条。
    /// </summary>
    /// <remarks>
    /// 0.1.18 的五个语言表里 <c>W*</c> 词条全部为 0 条，而 <c>Managers.GetText</c> 命中不到时
    /// 会返回 key 本身并打一条 <c>[Text] Missing TextData key</c> 错误 —— 所以必须先注入再渲染。
    /// <para>
    /// 只补**缺失**的键，绝不覆盖原版已有的词条（换语言、官方补回词条时都安全）。
    /// </para>
    /// </remarks>
    internal static class Localization
    {
        /// <summary>「无法判明具体设备」时用的通用活动词条。</summary>
        public const string GenericActivityKey = "ClueDetailDeviceAction";

        /// <summary>伪证页「活动」那一行的行标题（0.1.18 连 <c>What</c> 词条一起删了）。</summary>
        public const string WhatLabelKey = "ClueDetailWhatLabel";

        /// <summary>本模块自有词条（0.1.18 里没有对应概念，所以不进生成文件 LocalizationData）。</summary>
        private static readonly Dictionary<string, Dictionary<string, string>> Extra =
            new Dictionary<string, Dictionary<string, string>>
            {
                {
                    GenericActivityKey,
                    new Dictionary<string, string>
                    {
                        { "CHS", "操作了设备" },
                        { "CHT", "操作了設備" },
                        { "ENG", "used a device" },
                        { "JPN", "装置を操作した" },
                        { "KOR", "장치를 조작했" },
                    }
                },
                {
                    WhatLabelKey,
                    new Dictionary<string, string>
                    {
                        { "CHS", "活动" },
                        { "CHT", "活動" },
                        { "ENG", "Action" },
                        { "JPN", "行動" },
                        { "KOR", "행동" },
                    }
                },
            };

        private static bool _installed;
        private static string _genericActivity;
        private static string _whatLabel;

        /// <summary>通用活动文案；未注入完成时为 null。</summary>
        public static string GenericActivity => _genericActivity;

        /// <summary>伪证页「活动」行标题；未注入完成时为 null。</summary>
        public static string WhatLabel => _whatLabel;

        public static string CurrentLanguage()
        {
            try
            {
                return Managers.Language.ToString();
            }
            catch
            {
                return "ENG";
            }
        }

        /// <summary>幂等注入。数据表尚未加载时返回 false，调用方下次再试。</summary>
        public static bool Ensure()
        {
            if (_installed)
            {
                return true;
            }

            var data = Managers.Data;
            Dictionary<string, TextData> textDic = data?.TextDic;
            if (textDic == null || textDic.Count == 0)
            {
                return false;
            }

            string lang = CurrentLanguage();
            int injected = Inject(textDic, LocalizationData.Entries, lang);
            injected += Inject(textDic, Extra, lang);

            _genericActivity = Pick(Extra, GenericActivityKey, lang);
            _whatLabel = Pick(Extra, WhatLabelKey, lang);

            _installed = true;
            Log.Info($"注入旧版活动词条 {injected} 条（语言 {lang}）。");
            return true;
        }

        private static int Inject(Dictionary<string, TextData> textDic, Dictionary<string, Dictionary<string, string>> source, string lang)
        {
            int count = 0;
            foreach (var pair in source)
            {
                if (textDic.ContainsKey(pair.Key))
                {
                    continue;
                }

                string text = Pick(source, pair.Key, lang);
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                textDic[pair.Key] = new TextData { DataId = pair.Key, Text = text };
                count++;
            }

            return count;
        }

        private static string Pick(Dictionary<string, Dictionary<string, string>> source, string key, string lang)
        {
            if (!source.TryGetValue(key, out var entry) || entry == null)
            {
                return null;
            }

            if (entry.TryGetValue(lang, out string text) && !string.IsNullOrEmpty(text))
            {
                return text;
            }

            return entry.TryGetValue("ENG", out text) ? text : null;
        }
    }
}
