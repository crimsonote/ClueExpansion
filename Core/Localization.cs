using Data;

namespace ClueDetail.Core
{
    /// <summary>
    /// 把 0.1.18 从语言表整体删除的设备活动词条（46 条）注入回 <c>Managers.Data.TextDic</c>。
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
        private static bool _installed;

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
            if (data == null || data.TextDic == null || data.TextDic.Count == 0)
            {
                return false;
            }

            string lang = CurrentLanguage();
            int injected = 0;

            foreach (var pair in LocalizationData.Entries)
            {
                if (data.TextDic.ContainsKey(pair.Key))
                {
                    continue;
                }

                if (!pair.Value.TryGetValue(lang, out string text) || string.IsNullOrEmpty(text))
                {
                    if (!pair.Value.TryGetValue("ENG", out text))
                    {
                        continue;
                    }
                }

                data.TextDic[pair.Key] = new Data.TextData { DataId = pair.Key, Text = text };
                injected++;
            }

            _installed = true;
            Log.Info($"注入旧版活动词条 {injected} 条（语言 {lang}）。");
            return true;
        }
    }
}
