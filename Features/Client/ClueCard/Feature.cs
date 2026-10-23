using BepInEx.Configuration;
using ClueDetail.Core;

namespace ClueDetail.Features.Client.ClueCard
{
    /// <summary>
    /// 客户端侧：把线索卡片的排版还原成 0.1.17a 的样子。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 0.1.18 重做了 <c>UI_ClueSubItem</c> prefab（<c>common_prefab</c> bundle 里那份，
    /// 地址 <c>Assets/@Resources/Prefabs/UI/SubItem/UI_ClueSubItem.prefab</c>）：删掉了
    /// <c>ClueText</c>/<c>StatementText</c>（整句文本）与 <c>ClueWhatImage</c>/<c>StatementWhatImage</c>
    /// （活动卡面 = 设备图像），只留下「房间卡 + 房间名 + 分隔线 + 时间」。
    /// 于是 0.1.17a 的两个观感一起没了：
    /// </para>
    /// <list type="bullet">
    /// <item><b>不悬浮</b>：谁的头像 / 哪个房间 / <b>做了什么（设备图像）</b> / 什么时候；</item>
    /// <item><b>悬浮</b>：整行换成完整命题句（<c>Util.FormatProposition</c> 的结果）。</item>
    /// </list>
    /// <para>
    /// 本功能不碰 prefab，只在运行期往卡片实例里**补节点**：
    /// 克隆现成的 <c>ClueWhereImage</c> 当活动卡面、克隆 <c>ClueWhereText</c> 当整句文本框，
    /// 悬浮时把图标组藏起来、把整句显示出来。所有位置与宽度都是配置项 ——
    /// 观感可以只改 <c>.cfg</c> 调，不必重新编译。
    /// </para>
    /// <para>
    /// ⚠️ 活动卡面的图源沿用旧版口径：先试 <c>{活动}_{房间}_card</c>，再退回 <c>{活动}_card</c>
    /// （0.1.18 的资源还在，<c>Managers.Resource.Load</c> 命中不到就是没预载，此时该格留空并记一次日志）。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueCard",
        "客户端侧：还原旧版线索卡片的排版 —— 不悬浮时依次显示头像 / 房间 / 活动卡面（设备图像）/ 时间，悬浮时整行显示完整命题句。栏位位置与宽度全部可配置。",
        defaultEnabled: true,
        side: FeatureSide.Client)]
    internal partial class ClueCardFeature
    {
        /// <summary>是否补上「活动卡面」（设备图像）那一格。</summary>
        public static ConfigEntry<bool> ShowWhatCard { get; private set; }

        /// <summary>是否显示分隔竖线（0.1.18 新加的那条"黄条"；旧版图标视图里没有它）。</summary>
        public static ConfigEntry<bool> ShowLine { get; private set; }

        /// <summary>活动卡面的边长（正方形）。</summary>
        public static ConfigEntry<float> WhatCardSize { get; private set; }

        /// <summary>房间卡 / 房间名的横向中心（相对行中心）。</summary>
        public static ConfigEntry<float> RoomX { get; private set; }

        /// <summary>房间卡 / 房间名的宽度。</summary>
        public static ConfigEntry<float> RoomWidth { get; private set; }

        /// <summary>活动卡面的横向中心（相对行中心）。</summary>
        public static ConfigEntry<float> WhatX { get; private set; }

        /// <summary>
        /// 解析不出具体活动时，是否退化为「该房间存在的第一种设备」的卡面。
        /// </summary>
        /// <remarks>
        /// 默认开：宁可给一张**这个房间里确实有**的设备图，也不要在那一格留一片空白。
        /// 文本那边不跟着猜 —— 整句仍是通用文案（「操作了设备」）或原版句式。
        /// </remarks>
        public static ConfigEntry<bool> GuessWhatCard { get; private set; }

        /// <summary>分隔竖线的横向中心（相对行中心）。</summary>
        public static ConfigEntry<float> LineX { get; private set; }

        /// <summary>时间文本的横向中心（相对行中心）。</summary>
        public static ConfigEntry<float> WhenX { get; private set; }

        /// <summary>「提出命题」按钮的横向中心（旧版真值 +215；0.1.18 改成 +244）。</summary>
        public static ConfigEntry<float> SubmitX { get; private set; }

        /// <summary>「提出命题」按钮的宽度（旧版真值 136；0.1.18 改成 100）。</summary>
        public static ConfigEntry<float> SubmitWidth { get; private set; }

        /// <summary>「提出命题」按钮的高度（旧版真值 64；0.1.18 改成 80）。</summary>
        public static ConfigEntry<float> SubmitHeight { get; private set; }

        /// <summary>是否启用「悬浮显示整句」。</summary>
        public static ConfigEntry<bool> ShowSentence { get; private set; }

        /// <summary>整句文本框的横向中心（相对行中心）。</summary>
        public static ConfigEntry<float> SentenceX { get; private set; }

        /// <summary>整句文本框的宽度。</summary>
        public static ConfigEntry<float> SentenceWidth { get; private set; }

        /// <summary>整句的字号；0 = 沿用模板字号。</summary>
        public static ConfigEntry<float> SentenceFontSize { get; private set; }

        /// <summary>整句的水平对齐：Left / Center / Right（大小写不敏感）。</summary>
        public static ConfigEntry<string> SentenceAlignment { get; private set; }

        public static void Bind(ConfigFile config, string section)
        {
            ShowWhatCard = config.Bind(
                section,
                "ShowWhatCard",
                true,
                "是否补上「活动卡面」（= 这张线索对应设备的图像，旧版排版里在房间与时间之间）。\n" +
                "图源沿用旧版口径：先找 {活动}_{房间}_card，找不到再退回 {活动}_card。\n" +
                "解析不出活动时的行为见 GuessWhatCard；那一格彻底没图时留空。");

            WhatCardSize = config.Bind(
                section,
                "WhatCardSize",
                80f,
                new ConfigDescription(
                    "活动卡面的边长（正方形，单位 = prefab 像素）。\n真值 = 0.1.17a 的 ClueWhatImage 80×80。",
                    new AcceptableValueRange<float>(16f, 200f)));

            ShowLine = config.Bind(
                section,
                "ShowLine",
                true,
                "是否显示那条分隔竖线（黄条）。\n" +
                "0.1.17a 的旧 prefab 里也有这条线（ClueIconLine @-54，4×80），不是 0.1.18 新增。\n" +
                "想更贴近旧版观感就设成 false（关掉后图标视图与悬浮视图都不会出现它）。");

            GuessWhatCard = config.Bind(
                section,
                "GuessWhatCard",
                false,
                "解析不出具体活动时（房主没装本模块、或这个房间有好几种设备），是否退化为\n" +
                "「该房间存在的第一种设备」的卡面。\n" +
                "⚠️ 默认**关**：那种图对应的是「房间里有什么」，不是「这次用了哪台」——\n" +
                "开了会看到明显没被操作的设备（实机反馈过），所以宁缺勿滥。\n" +
                "确定想要「不留空」再打开它。");

            RoomX = config.Bind(
                section,
                "RoomX",
                -135f,
                new ConfigDescription(
                    "房间卡与房间名的横向中心（相对整行中心，向左为负）。",
                    new AcceptableValueRange<float>(-298f, 298f)));

            RoomWidth = config.Bind(
                section,
                "RoomWidth",
                140f,
                new ConfigDescription(
                    "房间卡与房间名的宽度。官方在 0.1.18 缩减过这一栏，想还原旧版观感就调大它。",
                    new AcceptableValueRange<float>(40f, 300f)));

            WhatX = config.Bind(
                section,
                "WhatX",
                -8f,
                new ConfigDescription(
                    "活动卡面的横向中心（相对整行中心）。\n真值 = 0.1.17a 的 ClueWhatImage @ (-8, 0)（0.1.18 删了这个节点）。",
                    new AcceptableValueRange<float>(-298f, 298f)));

            LineX = config.Bind(
                section,
                "LineX",
                -54f,
                new ConfigDescription(
                    "分隔竖线（那条黄条）的横向中心。\n真值 = -54（0.1.17a 与 0.1.18 都有，4×80，位于房间卡与活动卡面之间）。\n" +
                    "悬浮显示整句时它（以及整块图标区）会被隐藏。",
                    new AcceptableValueRange<float>(-298f, 298f)));

            WhenX = config.Bind(
                section,
                "WhenX",
                113f,
                new ConfigDescription(
                    "时间文本的横向中心。",
                    new AcceptableValueRange<float>(-298f, 298f)));

            SubmitX = config.Bind(
                section,
                "SubmitX",
                244f,
                new ConfigDescription(
                    "「提出命题」按钮的横向中心。默认 = **旧 prefab 真值 +215**（0.1.18 改成 +244）。",
                    new AcceptableValueRange<float>(-298f, 298f)));

            SubmitWidth = config.Bind(
                section,
                "SubmitWidth",
                100f,
                new ConfigDescription(
                    "「提出命题」按钮的宽度。默认 = **旧 prefab 真值 136**（0.1.18 改成 100）。",
                    new AcceptableValueRange<float>(40f, 300f)));

            SubmitHeight = config.Bind(
                section,
                "SubmitHeight",
                80f,
                new ConfigDescription(
                    "「提出命题」按钮的高度。默认 = **旧 prefab 真值 64**（0.1.18 改成 80）。",
                    new AcceptableValueRange<float>(20f, 200f)));

            ShowSentence = config.Bind(
                section,
                "ShowSentence",
                true,
                "是否启用「悬浮显示整句」：鼠标停在某条线索上时，头像右边的整块换成完整命题句\n" +
                "（与旧版一致；句子内容由 ClueRender 的详细活动一起决定）。");

            SentenceX = config.Bind(
                section,
                "SentenceX",
                -10f,
                new ConfigDescription(
                    "整句文本框的横向中心（相对整行中心）。默认 = 旧版 ClueText 节点的 -25.3。",
                    new AcceptableValueRange<float>(-298f, 298f)));

            SentenceWidth = config.Bind(
                section,
                "SentenceWidth",
                380f,
                new ConfigDescription(
                    "整句文本框的宽度。默认 = 旧版 ClueText 节点的 350（高度固定 100，同一份 prefab 值）。",
                    new AcceptableValueRange<float>(100f, 596f)));

            SentenceFontSize = config.Bind(
                section,
                "SentenceFontSize",
                16f,
                new ConfigDescription(
                    "整句的字号。默认 = 旧版 ClueText 节点的 m_fontSize=16。\n" +
                    "⚠️ 别沿用模板（房间名文本框）的字号：那是 35、自动缩放上限也是 35，写出来明显偏大。\n" +
                    "设成 0 = 完全不碰字号（沿用模板）。本项 > 0 时按旧版口径开自动缩放\n" +
                    "（min 5 / max = 本值），并把行距设成旧版的 45。",
                    new AcceptableValueRange<float>(0f, 60f)));

            SentenceAlignment = config.Bind(
                section,
                "SentenceAlignment",
                "Left",
                "整句的水平对齐：Left / Center / Right（大小写不敏感，认不出就用 Left）。旧版就是 Left。");

            MigrateLegacyDefaults(config);
        }

        /// <summary>
        /// 把「上一版默认值原封不动」的老 cfg 一次性搬到新默认值。
        /// </summary>
        /// <remarks>
        /// ⚠️ BepInEx 的配置值一旦落盘就**永远留着** —— 改代码里的默认值对已经跑过一局的用户
        /// **完全不起作用**（2026-10-18 实机踩到：整句字号停在上一版的 0，于是沿用房间名模板的
        /// 35 + 自动缩放，看起来"字更大"，而新默认的 16 根本没生效）。
        /// 判据取 <c>LineX == 15</c>：那个值只出现在前两版（WhatX=-40 / LineX=15 那套）的默认值里。
        /// </remarks>
        private static void MigrateLegacyDefaults(ConfigFile config)
        {
            if (LineX == null || global::System.Math.Abs(LineX.Value - 15f) > 0.01f)
            {
                return;
            }

            RoomX.Value = -135f;
            WhatX.Value = -8f;
            WhatCardSize.Value = 80f;
            ShowLine.Value = true;
            LineX.Value = -54f;
            WhenX.Value = 113f;
            SubmitX.Value = 244f;
            SubmitWidth.Value = 100f;
            SubmitHeight.Value = 80f;
            SentenceX.Value = -10f;
            SentenceWidth.Value = 380f;
            SentenceFontSize.Value = 16f;
            GuessWhatCard.Value = false;
            config.Save();

            global::ClueDetail.Core.Log.Info(
                "线索卡片：检测到配置文件里还是旧版默认布局，已按新默认值重置 [ClueCard] 段（仍可继续手调）。");
        }

        /// <summary>
        /// 热关闭：把补出来的节点撤掉，并把被我们藏起来的原版节点恢复显示。
        /// </summary>
        /// <remarks>
        /// 不撤的后果：卡片上还挂着「活动卡面」和整句文本框，但已经没有任何代码在刷新它们
        /// —— 玩家会看到一张永远停在旧内容上的卡片（AGENTS.md §11.3 的 OnDisabled 铁律）。
        /// </remarks>
        public static void OnDisabled()
        {
            ClueCardBuilder.ResetAll();
        }
    }
}
