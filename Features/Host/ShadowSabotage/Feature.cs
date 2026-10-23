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
        "未破坏时只有黑幕能按 E 破坏，破坏后所有人（含黑幕）可修，进度走原版摇汽水条；" +
        "破坏期间封锁其他一切破坏 —— 另一个破坏点同时进 CD，原版电闸窗口整体作废（图钉 / 平板条目撤掉），" +
        "修复完成 60 秒后重新派发。" +
        "身份识别组件被破坏期间，新线索一律记成「某人」；" +
        "时钟记录组件被破坏期间，新线索的时间冻结在破坏那一刻（不影响报告里的死亡时间）。" +
        "每次完整破坏都会追加一条「黑幕…切断了电力」的线索（同 30 秒窗口内合并）。",
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

        /// <summary>时间污染期间，按「拍摄节拍」给黑方/黑幕发 2 秒全屏变暗提醒。</summary>
        public static ConfigEntry<bool> BlackoutReminder { get; private set; }

        /// <summary>变暗提醒是否发给所有人（默认只发黑方/黑幕）。</summary>
        public static ConfigEntry<bool> BlackoutToAll { get; private set; }

        /// <summary>是否启用「身份识别组件」这个破坏点。</summary>
        public static ConfigEntry<bool> EnableIdentify { get; private set; }

        /// <summary>是否启用「时钟记录组件」这个破坏点。</summary>
        public static ConfigEntry<bool> EnableClock { get; private set; }

        /// <summary>黑方 / 黑幕破坏的进度条时长（秒）。</summary>
        public static ConfigEntry<float> BlackBreakSeconds { get; private set; }

        /// <summary>白方修复的进度条时长（秒）。</summary>
        public static ConfigEntry<float> WhiteRepairSeconds { get; private set; }

        /// <summary>黑方 / 黑幕破坏时是否播放「操作」动作。</summary>
        public static ConfigEntry<bool> BlackAction { get; private set; }

        /// <summary>白方修复时是否播放「操作」动作。</summary>
        public static ConfigEntry<bool> WhiteAction { get; private set; }

        /// <summary>假主人的进入半径（游戏单位）。进来才发，出去要更远（滞回）。</summary>
        public static ConfigEntry<float> InteractRadius { get; private set; }

        /// <summary>身份污染时「被记下的人」的占位符：<c>Someone</c> / <c>Black</c> / <c>Mastermind</c>。</summary>
        /// <remarks>用字符串而非枚举：BepInEx 的 <c>AcceptableValueList&lt;T&gt;</c> 要求 <c>IEquatable&lt;T&gt;</c>，枚举不满足。</remarks>
        public static ConfigEntry<string> Placeholder { get; private set; }

        /// <summary>占位符是「某人」时，是否用「黑」假线索预热控件头像。</summary>
        public static ConfigEntry<bool> Preheat { get; private set; }

        /// <summary>
        /// 时钟破坏延迟回放里的销毁帧处理：
        /// <c>true</c> = 死亡帧、firstHidden、firstHidden+10 三帧全部原样保留（最接近原版）；
        /// <c>false</c> = 只保留三个时间 key，死亡帧内容参与轮选。
        /// </summary>
        public static ConfigEntry<bool> TimelineKeepAllFrames { get; private set; }

        public static void Bind(ConfigFile config, string section)
        {
            PosIdentifyX = config.Bind(
                section,
                "PosIdentifyX",
                6580f,
                "身份识别组件破坏点的 X 坐标。默认 6580 = 科学室东西向**过道的最西端死头**（世界格 29,21；再往西 6510 是封死的竖墙）。2026-10-19 改：原默认 6608,4700 落在 Shelf 的实体碰撞盒内（x[6590,6952] y[4494,4784]），玩家走不过去也按不到 E。");

            PosIdentifyY = config.Bind(
                section,
                "PosIdentifyY",
                4900f,
                "身份识别组件破坏点的 Y 坐标。默认 4900 = 该过道可走带（y≈4790~5120）内的最西端，最近设备 Bio(6730,5033) 净空 200。");

            PosClockX = config.Bind(
                section,
                "PosClockX",
                1232f,
                "时钟记录组件破坏点的 X 坐标。\n" +
                "2026-10-23 起默认 1232 = 图书馆 warpMagic 右下方空地（tile 5,20 中心，世界坐标 1232,4592）。\n" +
                "旧默认 9561 是电脑室最右侧刀架下方。");

            PosClockY = config.Bind(
                section,
                "PosClockY",
                4592f,
                "时钟记录组件破坏点的 Y 坐标。\n" +
                "2026-10-23 起默认 4592 = 图书馆 warpMagic 右下方空地（tile 5,20 中心）。\n" +
                "旧默认 3430 是电脑室最右侧刀架下方。");

            BlackoutReminder = config.Bind(
                section,
                "BlackoutReminder",
                true,
                "时间破坏期间，每当「回放拍摄」命中每小时第二次（帧时间相对拍摄偏移 = 10 分钟）时，\n" +
                "给黑方 / 黑幕（或所有人，见 BlackoutToAll）发一次全屏变暗提醒。\n" +
                "客户端原版实现是 2 秒黑屏，中心约等于拍摄时刻 +1 秒。");

            BlackoutToAll = config.Bind(
                section,
                "BlackoutToAll",
                false,
                "变暗提醒是否发给所有人。\n" +
                "false（默认）= 只发黑方（Dark / Black）与黑幕；true = 所有存活玩家都收到。");

            EnableIdentify = config.Bind(
                section,
                "EnableIdentify",
                true,
                "是否启用「身份识别组件」这个破坏点。关掉后它不会被生成，也不会参与窗口派发。");

            EnableClock = config.Bind(
                section,
                "EnableClock",
                true,
                "是否启用「时钟记录组件」这个破坏点。关掉后它不会被生成，也不会参与窗口派发。");

            BlackBreakSeconds = config.Bind(
                section,
                "BlackBreakSeconds",
                10f,
                "**黑方 / 黑幕破坏**时，进度条从空推到满需要多少秒（默认 10）。\n" +
                "中途离开会保留已推进的进度。");

            WhiteRepairSeconds = config.Bind(
                section,
                "WhiteRepairSeconds",
                15f,
                "**白方修复**时，进度条从空推到满需要多少秒（默认 15）。\n" +
                "与 BlackBreakSeconds 独立配置。");

            BlackAction = config.Bind(
                section,
                "BlackAction",
                false,
                "黑方 / 黑幕破坏时是否播放「操作」动作（Casting / 8_Interact）。\n" +
                "false（默认）= 无动作：不把玩家状态改成 Casting，别人看不出他在动手。");

            WhiteAction = config.Bind(
                section,
                "WhiteAction",
                true,
                "白方修复时是否播放「操作」动作（Casting / 8_Interact）。\n" +
                "true（默认）= 有动作，别人能看到他在修。");

            InteractRadius = config.Bind(
                section,
                "InteractRadius",
                120f,
                "「假主人」的进入半径（游戏单位）。\n" +
                "服务端只在这个距离内把破坏点伪装成该玩家的专属小熊，客户端才会出现按 E 提示。\n" +
                "原版小熊自身的交互框是 150×150（±75），所以这里默认 120 比客户端判定更宽松。");

            NotifyBlackDelay = config.Bind(
                section,
                "NotifyBlackDelay",
                0,
                "破坏发生后，**黑方 / 黑幕** 多久看到指向破坏点的断电箭头（秒）。0 = 立即（默认）。\n" +
                "2026-10-20 新增：黑方阵营应当立刻知道哪里被断了电，白方延迟一段时间才发现。");

            NotifyWhiteDelay = config.Bind(
                section,
                "NotifyWhiteDelay",
                15,
                "破坏发生后，**白方**（非黑方阵营）多久看到断电箭头（秒）。0 = 立即。\n" +
                "默认 15 秒：白方要过一会儿才发现哪里被断了电。\n" +
                "⚠️ 黑白两档是**逐人单发**的，白方这条不会覆盖黑方已经收到的箭头。");

            Placeholder = config.Bind(
                section,
                "PollutionPlaceholder",
                "Someone",
                new ConfigDescription(
                    "身份破坏时，线索里「被记下的人」用什么占位符。\n" +
                    "Someone = 某人（id 0）：文本正确，但 0.1.18 漏注册 Someone_SD 图，\n" +
                    "          客户端加载失败 ⇒ 头像会残留上一条线索的真人头像（可用 PlaceholderPreheat 缓解）；\n" +
                    "Black = 黑（id 66613）：头像 Black_SD 可加载，但文本与活动词都变成黑方语义；\n" +
                    "Mastermind = 黑幕（id 11037）：头像 Mastermind_SD 可加载，文本变成黑幕语义。",
                    new AcceptableValueList<string>("Someone", "Black", "Mastermind")));

            Preheat = config.Bind(
                section,
                "PlaceholderPreheat",
                true,
                "仅当 PollutionPlaceholder = Someone 时有效（其它占位符的头像本来就能加载，不需要预热）。\n" +
                "做法：先发一条「黑」的假线索把控件刷成黑方头像，隔两帧删掉，再发真正的「某人」线索 ——\n" +
                "客户端按 Time 排序把两者放在同一位置、复用同一个控件，于是头像保持黑方图而不是真人。\n" +
                "⚠️ 代价：线索列表若正可见，可能闪过一帧黑方线索；列表未打开时预热不生效（头像仍残留）。");

            TimelineKeepAllFrames = config.Bind(
                section,
                "TimelineKeepAllFrames",
                true,
                "时钟破坏期间被延迟的回放帧里，销毁帧（死亡帧 / firstHidden / firstHidden+10）怎么处理：\n" +
                "true = 三帧全部原样保留，交给原版客户端划掉（观感最接近原版）；\n" +
                "false = 只保留三个时间 key，死亡帧的内容参与轮选（避免从 --:-- 的格子认出作案内容）。");
        }

        // ══════════════════ 生命周期 ══════════════════

        /// <summary>插件加载时把运行期状态清空（真正的重置在每局窗口开始时）。</summary>
        public static void OnLoaded()
        {
            BreakerStates.Reset();
            BreakBar.ClearAll();
        }

        // ══════════════════ 配置读取（Bind 失败时退回默认值） ══════════════════

        /// <summary>当前选定的占位符。</summary>
        internal static PollutionPlaceholder PlaceholderValue
        {
            get
            {
                string value = Placeholder != null ? Placeholder.Value : null;
                if (string.Equals(value, "Black", global::System.StringComparison.OrdinalIgnoreCase))
                {
                    return PollutionPlaceholder.Black;
                }

                if (string.Equals(value, "Mastermind", global::System.StringComparison.OrdinalIgnoreCase))
                {
                    return PollutionPlaceholder.Mastermind;
                }

                return PollutionPlaceholder.Someone;
            }
        }

        /// <summary>是否启用「预热」（仅 <see cref="PollutionPlaceholder.Someone"/> 有意义）。</summary>
        internal static bool PreheatValue =>
            PlaceholderValue == PollutionPlaceholder.Someone && Preheat != null && Preheat.Value;

        /// <summary>占位符对应的 <c>PlayerId</c>（0 / 66613 / 11037）。</summary>
        internal static int PlaceholderPlayerId
        {
            get
            {
                switch (PlaceholderValue)
                {
                    case PollutionPlaceholder.Black:
                        return 66613;
                    case PollutionPlaceholder.Mastermind:
                        return 11037;
                    default:
                        return 0;
                }
            }
        }

        internal static float IdentifyX => PosIdentifyX != null ? PosIdentifyX.Value : 6580f;
        internal static float IdentifyY => PosIdentifyY != null ? PosIdentifyY.Value : 4900f;
        internal static float ClockX => PosClockX != null ? PosClockX.Value : 1232f;
        internal static bool BlackoutReminderValue => BlackoutReminder != null && BlackoutReminder.Value;

        internal static bool BlackoutToAllValue => BlackoutToAll != null && BlackoutToAll.Value;
        internal static float ClockY => PosClockY != null ? PosClockY.Value : 4592f;

        /// <summary>「身份识别组件」是否启用（热读）。</summary>
        internal static bool EnableIdentifyValue => EnableIdentify == null || EnableIdentify.Value;

        /// <summary>「时钟记录组件」是否启用（热读）。</summary>
        internal static bool EnableClockValue => EnableClock == null || EnableClock.Value;

        /// <summary>黑方破坏时长（秒），热读。</summary>
        internal static float BlackBreakSecondsValue
            => BlackBreakSeconds != null ? BlackBreakSeconds.Value : 10f;

        /// <summary>白方修复时长（秒），热读。</summary>
        internal static float WhiteRepairSecondsValue
            => WhiteRepairSeconds != null ? WhiteRepairSeconds.Value : 15f;

        /// <summary>黑方破坏是否播放动作（热读）。</summary>
        internal static bool BlackActionValue => BlackAction != null && BlackAction.Value;

        /// <summary>白方修复是否播放动作（热读）。</summary>
        internal static bool WhiteActionValue => WhiteAction == null || WhiteAction.Value;

        /// <summary>黑方 / 黑幕的箭头延迟（秒），热读。</summary>
        internal static float NotifyBlackDelayValue => NotifyBlackDelay != null ? NotifyBlackDelay.Value : 0f;

        /// <summary>白方的箭头延迟（秒），热读。</summary>
        internal static float NotifyWhiteDelayValue => NotifyWhiteDelay != null ? NotifyWhiteDelay.Value : 15f;
        /// <summary>破坏后延迟多久发箭头（秒）。需求口径 2026-10-19 改为 12 秒，不给配置项。</summary>
        public static ConfigEntry<int> NotifyBlackDelay { get; private set; }

        /// <summary>破坏后，**白方**看到断电箭头的延迟（秒）。0 = 立即（= 旧行为）。</summary>
        public static ConfigEntry<int> NotifyWhiteDelay { get; private set; }

        /// <summary>把时长夹到合理范围（防止 cfg 写成 0 或负数导致进度瞬间满 / 永远不满）。</summary>
        private static float ClampSeconds(float v)
        {
            return v < 0.5f ? 0.5f : v;
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

        /// <summary>
        /// 破坏结束（修复完成）后多久重新派发破坏窗口（秒）。
        /// </summary>
        /// <remarks>
        /// 与原版「供电全部恢复 + 60 秒」同一个数（<c>AreaManager.RefreshLight</c> case 0，0.1.18 :176585），
        /// 但**必须我们自己起计时器**：我们的破坏不动光照，等不到那条信号。刻意不给配置项。
        /// </remarks>
        internal const int ReloadSeconds = 60;

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

    /// <summary>
    /// 身份破坏时，线索里「被记下的人」用什么占位符。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 客户端 <c>UI_ClueSubItem.FillText</c>（0.1.18 :86637-86667）的**文本与头像取自同一个**
    /// <c>Info.PlayerId</c>，但两者的分支条件**不一致**，导致三者各有缺陷（2026-10-19 实机核对）：
    /// </para>
    /// <list type="bullet">
    /// <item><b>文本</b>：<c>GetPlayerCache(id) == null</c> ⇒ 「某人」（任何缓存里没有的 id 都行）；</item>
    /// <item><b>头像</b>：<c>id == 0</c> ⇒ <c>Someone_SD</c>（**catalog 未注册** ⇒ 加载失败 ⇒ 保留上一条的头像）；
    /// <c>66613</c> ⇒ <c>Black_SD</c>（可加载）；<c>11037/11038</c> ⇒ <c>Mastermind_SD</c>（可加载）；
    /// 其余 ⇒ 查缓存，查不到就不更新。</item>
    /// </list>
    /// <para>
    /// 所以三者只能三选一：文本正确的没法加载图，能加载图的文本会被改写。
    /// </para>
    /// </remarks>
    public enum PollutionPlaceholder
    {
        /// <summary>某人（id 0）。文本正确；头像依赖预热或会残留上一条。</summary>
        Someone = 0,

        /// <summary>黑（id 66613）。头像 <c>Black_SD</c> 可加载；文本变成「黑」。</summary>
        Black = 1,

        /// <summary>黑幕（id 11037）。头像 <c>Mastermind_SD</c> 可加载；文本变成「黑幕」。</summary>
        Mastermind = 2,
    }
}