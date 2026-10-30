using BepInEx.Configuration;
using ClueDetail.Core;

namespace ClueDetail.Features.Host.MissionBacklash
{
    /// <summary>
    /// 实验性：黑方 / 黑幕完成的班级活动「反噬」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 口径（2026-10-30 用户定义）：
    /// <list type="number">
    /// <item>黑（<c>EPlayerColor.Black</c>）与黑幕（<c>EPlayerColor.Dark</c>）完成任务时，
    /// <b>原版那笔加分照常发生</b> —— <c>ClearMission</c> 会在同一帧把进度与时间加上
    /// （:169600 / :169613），本功能**不拦、也不回滚**它；本模块只额外记一笔账：
    /// 该任务「本应增加」的逃生进度与倒计时长。</item>
    /// <item>在每个<b>游戏整点</b>结算上一小时的记录，从全队进度与倒计时里扣掉
    /// <c>记录总量 × 倍率</c>（倍率默认 2）。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 所以玩家看到的是「黑方做完任务，进度条先涨；到一个整点，进度条再跌」，
    /// 默认倍率下的净效果是 <b>−1 倍</b>（原版 +1，周期末 −2）。倍率设 1 则净 0（只抵消）。
    /// </para>
    /// <para>
    /// ⚠️ 只对「黑方全体」生效：<c>Black || Dark</c>（0.1.18 :178434 的阵营判定口径，与
    /// AGENTS §4.9 一致；<c>Black</c> = 黑杀手、<c>Dark</c> = 黑幕，不能按名字猜）。
    /// 白方完成任务不受影响。
    /// </para>
    /// <para>
    /// 计时基准是**游戏时钟** <c>TimeManager.SurviveTime</c>（0.1.18 :181666，每秒 +1，
    /// 每局从 420 = 7:00 起算；<c>SurviveHour = SurviveTime / 60</c> 即游戏内小时）。
    /// 结算点固定为 <c>SurviveTime % 60 == 0</c> 的整点，每 <c>EveryHours</c> 个整点结算一次 ——
    /// 与「谁在第几秒进局」「黑方第几秒完成任务」都无关，**不会带进局偏移**
    /// （早前按「首次 Tick 对齐」实现，会永远偏 1 秒或更多，已改掉）。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueMissionBacklash",
        "实验性：黑与黑幕完成任务时，原版照常给该任务加进度与时间（本功能不改）；本模块只额外记账，\n" +
        "之后在每个游戏整点（每 N 个游戏小时，1 小时 = 60 秒，默认每 1 小时）结算上一小时的记录，\n" +
        "按倍率（默认 2 倍）扣掉记录总量。所以玩家看到的是「进度条先涨、到整点再跌」，\n" +
        "默认倍率下的净效果为 −1 倍（原版 +1，周期末 −2）。\n" +
        "结算点永远落在游戏时钟整点，不受进局时刻或任务完成时刻影响。\n" +
        "小时数、倍率、是否扣进度 / 扣时间都可调；段开关可热关，关闭时不清空已生效的游戏状态。",
        defaultEnabled: false,
        side: FeatureSide.Host)]
    internal partial class MissionBacklashFeature
    {
        /// <summary>每几个游戏小时结算一次。默认 1 = 每个整点。</summary>
        public static ConfigEntry<int> EveryHours { get; private set; }

        /// <summary>结算倍率。默认 2 = 扣掉记录量的两倍。</summary>
        public static ConfigEntry<float> Multiplier { get; private set; }

        /// <summary>是否扣逃生进度（逃脱计量表）。</summary>
        public static ConfigEntry<bool> PenalizeProgress { get; private set; }

        /// <summary>是否扣倒计时。</summary>
        public static ConfigEntry<bool> PenalizeTime { get; private set; }

        // ── 运行期状态（全部在 Unity 主线程读写） ──

        /// <summary>已结算的最后一个整点（<c>SurviveTime / 60</c>）；<c>-1</c> 表示本局还没结算过。</summary>
        internal static long LastSettledHour = -1;

        /// <summary>本周期内黑方完成任务累计的「应加进度」（未乘倍率）。</summary>
        internal static double PendingProgress;

        /// <summary>本周期内黑方完成任务累计的「应加秒数」（未乘倍率）。</summary>
        internal static double PendingSeconds;

        /// <summary>小时数下限（1 = 每个整点）。</summary>
        internal const int MinHours = 1;

        /// <summary>小时数上限（一局最长也只有几分钟，12 小时足够宽）。</summary>
        internal const int MaxHours = 12;

        public static void Bind(ConfigFile config, string section)
        {
            EveryHours = config.Bind(
                section,
                "EveryHours",
                1,
                "每几个游戏小时结算一次。默认 1 = 每个整点（1 小时 = 60 秒，即 SurviveTime % 60 == 0）。\n" +
                "结算点永远落在游戏时钟的整点（7:00、8:00、9:00……），\n" +
                "不会因为进局时刻或任务完成时刻产生偏移。\n" +
                "允许 1–12；超出范围按边界处理。");

            Multiplier = config.Bind(
                section,
                "Multiplier",
                2f,
                "结算倍率。默认 2 = 扣掉上一小时记录总量的两倍。\n" +
                "1 = 只抵消黑方任务本应加的量（净效果为 0）；0.5 = 只扣一半。\n" +
                "允许 0–5；超出范围按边界处理。");

            PenalizeProgress = config.Bind(
                section,
                "PenalizeProgress",
                true,
                "是否扣逃生进度（逃脱计量表）。\n" +
                "false = 只扣倒计时，进度不受影响。");

            PenalizeTime = config.Bind(
                section,
                "PenalizeTime",
                true,
                "是否扣倒计时。\n" +
                "false = 只扣进度，倒计时不受影响。\n" +
                "⚠️ 开着的代价：时间扣到 0 时原版会立刻判定生存阶段结束（无未发现尸体则黑方胜），\n" +
                "这是原版结算，不是本模块加的。");
        }

        /// <summary>启动时清零（真正的按局重置在每次 ResetSurvival 时做）。</summary>
        public static void OnLoaded()
        {
            Reset();
        }

        /// <summary>热关闭：把本周期未结算的记录丢掉，避免下次开启时把旧账一起扣。</summary>
        public static void OnDisabled()
        {
            Reset();
        }

        internal static void Reset()
        {
            LastSettledHour = -1;
            PendingProgress = 0d;
            PendingSeconds = 0d;
        }

        /// <summary>实际生效的小时数（读配置并夹到 [Min, Max]）。</summary>
        internal static int EveryHoursValue
        {
            get
            {
                int value = EveryHours != null ? EveryHours.Value : 1;
                if (value < MinHours)
                {
                    return MinHours;
                }

                return value > MaxHours ? MaxHours : value;
            }
        }

        /// <summary>实际生效的倍率（读配置并夹到 [0, 5]）。</summary>
        internal static float MultiplierValue
        {
            get
            {
                float value = Multiplier != null ? Multiplier.Value : 2f;
                // cfg 里手写 NaN / Infinity 时 BepInEx 会原样解析，clamp 比较对它们不成立，
                // 所以必须先挡掉，否则倍率会原样传进扣减公式。
                if (float.IsNaN(value) || float.IsInfinity(value))
                {
                    return 2f;
                }

                if (value < 0f)
                {
                    return 0f;
                }

                return value > 5f ? 5f : value;
            }
        }

        internal static bool PenalizeProgressValue => PenalizeProgress == null || PenalizeProgress.Value;

        internal static bool PenalizeTimeValue => PenalizeTime == null || PenalizeTime.Value;
    }
}
