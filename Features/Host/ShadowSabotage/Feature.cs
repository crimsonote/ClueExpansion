using BepInEx.Configuration;
using ClueDetail.Core;

namespace ClueDetail.Features.Host.ShadowSabotage
{
    /// <summary>
    /// 隐蔽破坏点：两处**伪装成小熊**的服务端破坏点（科学室「身份识别组件」、电脑室「时钟记录组件」）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 语义（2026-10-18 定，详见 <c>.tmps/隐蔽破坏点-规划.md</c> §13）：
    /// </para>
    /// <list type="bullet">
    /// <item>实体是自建的 <see cref="BreakerDevice"/>（<c>Server.Game.Device</c> 子类），
    /// 设备类型报 <c>EDeviceType.Summon</c> 以复用原版小熊外观；</item>
    /// <item>**不走 <c>DeviceManager.CreateSummon</c>** —— 一旦进 <c>_summons</c> 就会被凛的附身技能
    /// （<c>SkillComponent.PossessMarionette</c>）与其它模块「认领」；</item>
    /// <item>未破坏时**只有黑幕**能按 E 破坏；破坏后**所有人**可修复。两者都用原版摇汽水进度条；</item>
    /// <item>与电闸**共用那条 60 秒 CD**：挂 <c>DeviceManager.StartFuseboxSabotage</c> 的 Postfix
    /// （0.1.18 :166828），它一被调用就说明「新一轮可破坏窗口开启」；</item>
    /// <item>CD 中（<see cref="BreakerState.CanBreak"/> 为 false）**不可交互**，也不在地图上标电闸图钉；
    /// **小熊本体始终在场**，以便留到调查阶段。</item>
    /// </list>
    /// <para>
    /// 纯服务端：未装本模块的客户端看到的是一只原版小熊，`StateList[1]` 的「假主人」它照样认，
    /// 所以「按 E」的提示与发包链路完全走原版。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueShadowSabotage",
        "隐蔽破坏点（纯服务端）：在地图两处放两只伪装成小熊的破坏点。" +
        "未破坏时只有黑幕能按 E 破坏，破坏后所有人可修，进度走原版摇汽水条；" +
        "与电闸共用同一条 60 秒 CD（电闸重派发时才恢复可破坏）。" +
        "身份识别组件被破坏期间，新线索一律记成「某人」；" +
        "时钟记录组件被破坏期间，新线索的时间冻结在破坏那一刻（不影响报告里的死亡时间）。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal partial class ShadowSabotageFeature
    {
        // ══════════════════ 配置 ══════════════════

        /// <summary>身份识别组件破坏点的 X 坐标（科学室最左）。</summary>
        public static ConfigEntry<float> PosIdentifyX { get; private set; }

        /// <summary>身份识别组件破坏点的 Y 坐标（科学室最左）。</summary>
        public static ConfigEntry<float> PosIdentifyY { get; private set; }

        /// <summary>时钟记录组件破坏点的 X 坐标（电脑室刀架下）。</summary>
        public static ConfigEntry<float> PosClockX { get; private set; }

        /// <summary>时钟记录组件破坏点的 Y 坐标（电脑室刀架下）。</summary>
        public static ConfigEntry<float> PosClockY { get; private set; }

        /// <summary>进度条从空推到满需要多少秒（破坏与修复共用）。</summary>
        public static ConfigEntry<float> BreakSeconds { get; private set; }

        /// <summary>假主人的进入半径（游戏单位）。进来才发，出去要更远（滞回）。</summary>
        public static ConfigEntry<float> InteractRadius { get; private set; }

        public static void Bind(ConfigFile config, string section)
        {
            PosIdentifyX = config.Bind(
                section,
                "PosIdentifyX",
                6608f,
                "身份识别组件破坏点的 X 坐标。默认 6608 是科学室最左（格 25,15）。");

            PosIdentifyY = config.Bind(
                section,
                "PosIdentifyY",
                4700f,
                "身份识别组件破坏点的 Y 坐标。默认 4700 是科学室最左（格 25,15）。");

            PosClockX = config.Bind(
                section,
                "PosClockX",
                9561f,
                "时钟记录组件破坏点的 X 坐标。默认 9561 是电脑室最右侧刀架下方。");

            PosClockY = config.Bind(
                section,
                "PosClockY",
                3430f,
                "时钟记录组件破坏点的 Y 坐标。默认 3430 是电脑室最右侧刀架下方。");

            BreakSeconds = config.Bind(
                section,
                "BreakSeconds",
                5f,
                "进度条从空推到满需要多少秒。破坏与修复共用这个时长；中途离开会保留已推进的进度。");

            InteractRadius = config.Bind(
                section,
                "InteractRadius",
                120f,
                "「假主人」的进入半径（游戏单位）。\n" +
                "服务端只在这个距离内把破坏点伪装成该玩家的专属小熊，客户端才会出现按 E 提示。\n" +
                "原版小熊自身的交互框是 150×150（±75），所以这里默认 120 比客户端判定更宽松。");
        }

        // ══════════════════ 生命周期 ══════════════════

        /// <summary>插件加载时把运行期状态清空（真正的重置在每局窗口开始时）。</summary>
        public static void OnLoaded()
        {
            BreakerStates.Reset();
            BreakBar.ClearAll();
        }

        // ══════════════════ 配置读取（Bind 失败时退回默认值） ══════════════════

        internal static float IdentifyX => PosIdentifyX != null ? PosIdentifyX.Value : 6608f;
        internal static float IdentifyY => PosIdentifyY != null ? PosIdentifyY.Value : 4700f;
        internal static float ClockX => PosClockX != null ? PosClockX.Value : 9561f;
        internal static float ClockY => PosClockY != null ? PosClockY.Value : 3430f;
        internal static float BreakSecondsValue => PosBreakSeconds;
        /// <summary>破坏后延迟多久发箭头（秒）。需求口径就是 10 秒，不给配置项。</summary>
        internal const float NotifyDelayValue = 10f;

        private static float PosBreakSeconds
        {
            get
            {
                float v = BreakSeconds != null ? BreakSeconds.Value : 5f;
                return v < 0.5f ? 0.5f : v;
            }
        }

        internal static float EnterRadiusSq
        {
            get
            {
                float r = InteractRadius != null ? InteractRadius.Value : 120f;
                return r * r;
            }
        }

        /// <summary>
        /// 「假主人」的**离开**半径（平方）：固定为进入半径的 4/3。
        /// </summary>
        /// <remarks>
        /// 滞回只是为了避免玩家站在边界上时提示反复闪烁 —— 它是内部节拍，不是手感参数，
        /// 所以刻意不给它单独的配置项（少一个会被调歪的键）。
        /// </remarks>
        internal static float ExitRadiusSq
        {
            get
            {
                float enter = InteractRadius != null ? InteractRadius.Value : 120f;
                float r = enter * 4f / 3f;
                return r * r;
            }
        }

        /// <summary>范围内重发「假主人」的间隔（秒）：兜底丢包与入场动画期，属内部节拍。</summary>
        internal const float ResendSeconds = 1.5f;

        // ══════════════════ 常量 ══════════════════

        /// <summary>进度条用的物品 ID：1039 = SHAKER_BALL，全部物品里唯一带 UI_ShakingSlider 进度条的那个。</summary>
        internal const int BarItemId = 1039;

        /// <summary>进度条刻度硬上限。客户端 <c>UI_ShakingSlider.SetInfo</c> 是 <c>value / 24f</c>，24 是写死的。</summary>
        internal const int BarMax = 24;

        /// <summary>进度条刻度下发节拍（毫秒）。原版进度条没有补间，靠这个节拍连发。</summary>
        internal const int BarStepMs = 100;

        /// <summary>
        /// 两个破坏点的设备 ID。
        /// </summary>
        /// <remarks>
        /// 刻意取 90 万段：客户端的 <c>Managers.Device.HandleAddDevice</c>（0.1.18 :28247）按
        /// <c>Managers.Data.DeviceDic.TryGetValue(info.DeviceId)</c> 决定用哪个 prefab ——
        /// 命中就用那张表的 <c>DevicePrefabName</c>，**没命中才退回 <c>info.Type.ToString()</c>**
        /// （= "Summon"，也就是小熊 prefab）。
        /// <para>
        /// 装备／物品表的 DataId 都在 5 位以内（刀架 10080），而 <c>ObjectUtils.GenerateNewId()</c>
        /// （原版小熊用的那个）从 16 起递增、每局 <c>Clear()</c> 还会重置 ——
        /// 我们的设备跨局常驻，用那个计数器迟早会撞上，所以自取一段远离一切原版 id 的固定号。
        /// </para>
        /// </remarks>
        internal const int IdentifyDeviceId = 900101;

        /// <inheritdoc cref="IdentifyDeviceId"/>
        internal const int ClockDeviceId = 900102;
    }
}
