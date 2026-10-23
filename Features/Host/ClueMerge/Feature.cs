using BepInEx.Configuration;
using ClueDetail.Core;

namespace ClueDetail.Features.Host.ClueMerge
{
    /// <summary>
    /// 房主侧：把原版写死的「线索合并窗口」（30 秒桶）变成可配置。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 原版 <c>Device.RecordLastUsingPlayer</c>（0.1.18 :166404）用
    /// <c>limitTime = surviveTime / 30 * 30</c> 决定"这次使用是刷新上一条，还是新起一条"。
    /// 本功能只在配置**不是 30** 时接管该方法：
    /// </para>
    /// <list type="bullet">
    /// <item><b>30（默认）</b>：<see cref="Patch.MergeWindowHook"/> 直接返回 <c>true</c> ⇒ 原版方法原样跑，
    /// <b>一行都不碰</b>（风险为零）；</item>
    /// <item><b>0</b>：禁用合并 ⇒ 每次使用都新起一条；</item>
    /// <item><b>其它值</b>：自定义桶长（越大，窗口内被合并掉的线索越多）。</item>
    /// </list>
    /// <para>
    /// 接管时**逐字复刻**原版逻辑（身份映射、<c>S_REMOVE_CLUE</c>+<c>S_ADD_CLUE</c> 的顺序、给触发者发
    /// <c>S_LEFT_CLUE</c>），只把桶长换成 <see cref="ClueMergeWindow.LimitTime"/>；
    /// 并显式补上两个 Postfix 的职责（接管后它们不会跑）：未知字段 <b>100</b>（详细活动）与
    /// <b>101/103</b>（显示时刻 / 真实当事人）。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueMergeWindow",
        "房主侧：可配置线索合并窗口 —— 30 = 原版（30 秒桶内同一设备同一身份合并成一条）；0 = 禁用合并（每次使用都新起一条）；其它值 = 自定义秒数（越大覆盖越多线索）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal partial class ClueMergeFeature
    {
        /// <summary>线索合并窗口（秒）。30 = 原版行为（本功能不接管）。</summary>
        public static ConfigEntry<int> Seconds { get; private set; }

        public static void Bind(ConfigFile config, string section)
        {
            Seconds = config.Bind(
                section,
                "Seconds",
                30,
                new ConfigDescription(
                    "线索合并窗口（秒）。\n" +
                    "原版写死 30：同一台设备 + 同一身份 + 同一个 30 秒桶内反复使用，只留一条（时间刷成最后一次）。\n" +
                    "· 30（默认）= 原版行为，本功能完全不接管原方法；\n" +
                    "· 0 = 禁用合并，每次使用都新起一条线索；\n" +
                    "· 其它值（例如 60）= 自定义桶长，窗口越大被合并掉的线索越多。\n" +
                    "⚠️ 窗口是「滚动桶」（surviveTime ÷ 秒数 × 秒数），不是滑动窗口；桶边界对齐生存阶段计时，\n" +
                    "与系统时间无关。0.1.17a 与 0.1.18 的原版写法逐字相同（:165655 / :166404）。",
                    new AcceptableValueRange<int>(0, 600)));

            ClueMergeWindow.Provider = () => Seconds != null ? Seconds.Value : 30;
        }
    }
}
