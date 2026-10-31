using System.Collections.Generic;
using BepInEx.Configuration;
using ClueDetail.Core;

namespace ClueDetail.Features.Host.GhostPresence
{
    /// <summary>
    /// 彩蛋补丁：鬼魂临场感（铃铛伪装 + 真鬼间歇显形 + 安魂曲）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 口径（2026-10-31 用户定义）：
    /// <list type="number">
    /// <item><b>铃铛伪装</b>：生者手持铃铛（默认 <c>DataId = 3009</c>）时，在**其他人**视野里显示为幽灵；
    /// 本人视角不变。只在生存阶段生效，丢铃铛 / 换手 / 进入调查与审判立即解除。</item>
    /// <item><b>真鬼间歇显形</b>：已死的幽灵每隔一段随机时间，在所有人（含调查阶段）视野里
    /// 出现 2–4 秒再消失。**不改变生死**，只切 <c>State</c> 与可见性。</item>
    /// <item><b>安魂曲</b>：真鬼显形期间，全场按固定节奏听到 <c>HandBellSfx</c>（所有人可闻）。</item>
    /// </list>
    /// </para>
    /// <para>
    /// ⚠ 服务端真值永远不动：`IsAlive`、`PublicInfo.IsGhost`、`State` 的权威值保持原样。
    /// 伪装与显形都只在**出站副本**上改 <c>IsGhost</c>，或对特定观察者补 <c>S_SPAWN</c>。
    /// </para>
    /// <para>
    /// ⚠ 审判/投票阶段强制按活人处理：<c>State != Survive</c> 时清空伪装集合并恢复真身，
    /// 避免"死人票"与身份错乱。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueGhostPresence",
        "彩蛋：鬼魂临场感。① 生者手持铃铛时在别人视野中显示为幽灵（本人视角正常，丢铃铛即解除，仅生存阶段）；" +
        "② 真鬼每隔 20–35 秒在所有人视野中显形 2–4 秒（含调查阶段，不改生死）；" +
        "③ 显形期间全场按节奏响起铃铛安魂曲。三个子项独立开关。",
        defaultEnabled: false,
        side: FeatureSide.Host)]
    internal partial class GhostPresenceFeature
    {
        /// <summary>铃铛伪装总开关。</summary>
        public static ConfigEntry<bool> Disguise { get; private set; }

        /// <summary>真鬼间歇显形开关。</summary>
        public static ConfigEntry<bool> Reveal { get; private set; }

        /// <summary>安魂曲开关。</summary>
        public static ConfigEntry<bool> Requiem { get; private set; }

        /// <summary>铃铛的物品 DataId。</summary>
        public static ConfigEntry<int> BellItemId { get; private set; }

        /// <summary>两次显形之间的最小间隔（秒）。</summary>
        public static ConfigEntry<int> RevealIntervalMin { get; private set; }

        /// <summary>两次显形之间的最大间隔（秒）。</summary>
        public static ConfigEntry<int> RevealIntervalMax { get; private set; }

        /// <summary>单次显形的最短时长（秒）。</summary>
        public static ConfigEntry<int> RevealDurationMin { get; private set; }

        /// <summary>单次显形的最长时长（秒）。</summary>
        public static ConfigEntry<int> RevealDurationMax { get; private set; }

        /// <summary>安魂曲两声之间的间隔（秒）。</summary>
        public static ConfigEntry<float> RequiemGap { get; private set; }

        // ── 运行期状态（Unity 主线程） ──

        /// <summary>当前处于「铃铛伪装」的玩家 id 集合。服务端真值不变，只影响出站副本。</summary>
        internal static readonly HashSet<int> Disguised = new HashSet<int>();

        /// <summary>真鬼显形窗口：玩家 id → 结束 tick（<c>SurviveTime</c>）。</summary>
        internal static readonly Dictionary<int, long> RevealUntil = new Dictionary<int, long>();

        /// <summary>下一次显形时刻：玩家 id → tick。</summary>
        internal static readonly Dictionary<int, long> NextRevealAt = new Dictionary<int, long>();

        /// <summary>下一次安魂曲时刻：玩家 id → tick。</summary>
        internal static readonly Dictionary<int, long> NextBellAt = new Dictionary<int, long>();

        public static void Bind(ConfigFile config, string section)
        {
            Disguise = config.Bind(
                section,
                "Disguise",
                true,
                "铃铛伪装：生者手持铃铛时，在其他人视野中显示为幽灵。\n" +
                "本人视角不变；丢铃铛 / 换手立即解除；只在生存阶段生效。");

            Reveal = config.Bind(
                section,
                "Reveal",
                true,
                "真鬼间歇显形：已死的幽灵每隔一段时间在所有人视野中出现几秒。\n" +
                "不改变生死状态，只切换显示。调查阶段同样生效。");

            Requiem = config.Bind(
                section,
                "Requiem",
                true,
                "安魂曲：真鬼显形期间，全场按节奏听到铃铛声（所有人可闻）。\n" +
                "嫌吵可以关掉，不影响显形本身。");

            BellItemId = config.Bind(
                section,
                "BellItemId",
                3009,
                "铃铛的物品 DataId（原版 Bell = 3009）。");

            RevealIntervalMin = config.Bind(
                section,
                "RevealIntervalMin",
                20,
                "两次显形之间的最小间隔（秒）。");

            RevealIntervalMax = config.Bind(
                section,
                "RevealIntervalMax",
                35,
                "两次显形之间的最大间隔（秒）。");

            RevealDurationMin = config.Bind(
                section,
                "RevealDurationMin",
                2,
                "单次显形的最短时长（秒）。");

            RevealDurationMax = config.Bind(
                section,
                "RevealDurationMax",
                4,
                "单次显形的最长时长（秒）。");

            RequiemGap = config.Bind(
                section,
                "RequiemGap",
                1.1f,
                "安魂曲两声铃铛之间的间隔（秒）。\n" +
                "显形窗口内按这个间隔循环播放。");
        }

        public static void OnLoaded()
        {
            Reset();
        }

        public static void OnDisabled()
        {
            Reset();
        }

        internal static void Reset()
        {
            Disguised.Clear();
            RevealUntil.Clear();
            NextRevealAt.Clear();
            NextBellAt.Clear();
        }

        internal static bool DisguiseOn => Disguise == null || Disguise.Value;

        internal static bool RevealOn => Reveal == null || Reveal.Value;

        internal static bool RequiemOn => Requiem == null || Requiem.Value;

        internal static int BellId => BellItemId != null ? BellItemId.Value : 3009;

        internal static float RequiemGapValue => RequiemGap != null ? RequiemGap.Value : 1.1f;
    }
}
