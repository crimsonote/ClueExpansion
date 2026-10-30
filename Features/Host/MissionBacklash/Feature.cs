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
    /// <b>不立刻扣</b>，只记录该任务「本应增加」的逃生进度与倒计时长；</item>
    /// <item>在每个周期（默认 1 游戏小时 = 60 秒生存时间）的<b>开始</b>结算上一周期的记录，
    /// 从全队进度与倒计时里扣掉 <c>记录总量 × 倍率</c>（倍率默认 2）。</item>
    /// </list>
    /// </para>
    /// <para>
    /// ⚠️ 只对「黑方全体」生效：<c>Black || Dark</c>（0.1.18 :178434 的阵营判定口径，与
    /// AGENTS §4.9 一致；<c>Black</c> = 黑杀手、<c>Dark</c> = 黑幕，不能按名字猜）。
    /// 白方完成任务不受影响。
    /// </para>
    /// <para>
    /// 计时基准是 <c>TimeManager.SurviveTime</c>（0.1.18 :181666，每秒 +1，每局从 420 起算），
    /// 周期从「本局第一次进入生存阶段」开始对齐，默认 60 秒正好等于游戏内 1 小时。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueMissionBacklash",
        "实验性：黑与黑幕完成任务时，不立即生效，而是记录该任务本应增加的逃生进度与倒计时；" +
        "之后每个周期（默认 1 游戏小时 = 60 秒）开始时，扣掉上一周期记录总量 × 倍率（默认 2 倍）的进度与时间。\n" +
        "周期、倍率、是否扣进度 / 扣时间都可调；段开关可热关，关闭时不清空已生效的游戏状态。",
        defaultEnabled: false,
        side: FeatureSide.Host)]
    internal partial class MissionBacklashFeature
    {
        /// <summary>结算周期（游戏秒）。默认 60 = 游戏内 1 小时（<c>SurviveTime / 60</c>）。</summary>
        public static ConfigEntry<int> PeriodSeconds { get; private set; }

        /// <summary>结算倍率。默认 2 = 扣掉记录量的两倍。</summary>
        public static ConfigEntry<float> Multiplier { get; private set; }

        /// <summary>是否扣逃生进度（逃脱计量表）。</summary>
        public static ConfigEntry<bool> PenalizeProgress { get; private set; }

        /// <summary>是否扣倒计时。</summary>
        public static ConfigEntry<bool> PenalizeTime { get; private set; }

        // ── 运行期状态（全部在 Unity 主线程读写） ──

        /// <summary>当前结算周期的起始 tick；<c>-1</c> 表示尚未对齐（等待进入生存阶段）。</summary>
        internal static long BucketStart = -1;

        /// <summary>本周期内黑方完成任务累计的「应加进度」（未乘倍率）。</summary>
        internal static double PendingProgress;

        /// <summary>本周期内黑方完成任务累计的「应加秒数」（未乘倍率）。</summary>
        internal static double PendingSeconds;

        /// <summary>周期下限，防止误填 0 / 负数导致每秒结算。</summary>
        internal const int MinPeriodSeconds = 5;

        /// <summary>周期上限（一局最长也只有几分钟，600 秒足够）。</summary>
        internal const int MaxPeriodSeconds = 600;

        public static void Bind(ConfigFile config, string section)
        {
            PeriodSeconds = config.Bind(
                section,
                "PeriodSeconds",
                60,
                "结算周期（游戏秒）。默认 60 = 游戏内 1 小时（原版 SurviveHour = SurviveTime / 60）。\n" +
                "每个周期开始时，结算上一周期黑方完成任务所记录的进度与时间。\n" +
                "允许 5–600；超出范围按边界处理。");

            Multiplier = config.Bind(
                section,
                "Multiplier",
                2f,
                "结算倍率。默认 2 = 扣掉上一周期记录总量的两倍。\n" +
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

        /// <summary>启动时清零（真正的按局重置在每次进入生存阶段时做）。</summary>
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
            BucketStart = -1;
            PendingProgress = 0d;
            PendingSeconds = 0d;
        }

        /// <summary>实际生效的周期（读配置并夹到 [Min, Max]）。</summary>
        internal static int PeriodValue
        {
            get
            {
                int value = PeriodSeconds != null ? PeriodSeconds.Value : 60;
                if (value < MinPeriodSeconds)
                {
                    return MinPeriodSeconds;
                }

                return value > MaxPeriodSeconds ? MaxPeriodSeconds : value;
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
