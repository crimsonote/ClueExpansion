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

        /// <summary>活动卡面的边长（正方形）。</summary>
        public static ConfigEntry<float> WhatCardSize { get; private set; }

        /// <summary>房间卡 / 房间名的横向中心（相对行中心）。</summary>
        public static ConfigEntry<float> RoomX { get; private set; }

        /// <summary>房间卡 / 房间名的宽度。</summary>
        public static ConfigEntry<float> RoomWidth { get; private set; }

        /// <summary>活动卡面的横向中心（相对行中心）。</summary>
        public static ConfigEntry<float> WhatX { get; private set; }

        /// <summary>分隔竖线的横向中心（相对行中心）。</summary>
        public static ConfigEntry<float> LineX { get; private set; }

        /// <summary>时间文本的横向中心（相对行中心）。</summary>
        public static ConfigEntry<float> WhenX { get; private set; }

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
                "房主没装本模块、线索也推断不出活动时，这一格自动留空。");

            WhatCardSize = config.Bind(
                section,
                "WhatCardSize",
                72f,
                new ConfigDescription(
                    "活动卡面的边长（正方形，单位 = prefab 像素）。",
                    new AcceptableValueRange<float>(16f, 200f)));

            RoomX = config.Bind(
                section,
                "RoomX",
                -155f,
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
                -40f,
                new ConfigDescription(
                    "活动卡面的横向中心（相对整行中心）。默认落在房间与分隔线之间。",
                    new AcceptableValueRange<float>(-298f, 298f)));

            LineX = config.Bind(
                section,
                "LineX",
                15f,
                new ConfigDescription(
                    "分隔竖线的横向中心。补上活动卡面后，它和时间一起让位给新的一格。",
                    new AcceptableValueRange<float>(-298f, 298f)));

            WhenX = config.Bind(
                section,
                "WhenX",
                95f,
                new ConfigDescription(
                    "时间文本的横向中心。",
                    new AcceptableValueRange<float>(-298f, 298f)));

            ShowSentence = config.Bind(
                section,
                "ShowSentence",
                true,
                "是否启用「悬浮显示整句」：鼠标停在某条线索上时，头像右边的整块换成完整命题句\n" +
                "（与旧版一致；句子内容由 ClueRender 的详细活动一起决定）。");

            SentenceX = config.Bind(
                section,
                "SentenceX",
                -2f,
                new ConfigDescription(
                    "整句文本框的横向中心（相对整行中心）。",
                    new AcceptableValueRange<float>(-298f, 298f)));

            SentenceWidth = config.Bind(
                section,
                "SentenceWidth",
                380f,
                new ConfigDescription(
                    "整句文本框的宽度。默认刚好占满头像与「提出命题」按钮之间的空档。",
                    new AcceptableValueRange<float>(100f, 596f)));

            SentenceFontSize = config.Bind(
                section,
                "SentenceFontSize",
                0f,
                new ConfigDescription(
                    "整句的字号；0 = 沿用模板（房间名文本框）的字号。",
                    new AcceptableValueRange<float>(0f, 60f)));

            SentenceAlignment = config.Bind(
                section,
                "SentenceAlignment",
                "Left",
                "整句的水平对齐：Left / Center / Right（大小写不敏感，认不出就用 Left）。");
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
