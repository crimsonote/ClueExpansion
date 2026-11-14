using BepInEx.Configuration;
using ClueDetail.Core;

namespace ClueDetail.Features.Host.ClueFake
{
    /// <summary>
    /// 房主侧：黑方 / 黑幕的局内能力「线索伪造」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>入口</b>：只能在<b>发信机（聊天设备）的聊天频道</b>里输入 <c>/f</c>。
    /// 原版 <c>HostPacketHandler.RelayDeviceChat</c>（0.1.18 :177827）已经限定了
    /// 「仅 <c>Survive</c>、存活、且 <c>ChatDevice.IsOccupiedBy(sender)</c>」——
    /// 我们复刻同一组判据，不额外放宽。
    /// </para>
    /// <para>
    /// <b>产物</b>：向目标房间的设备 <c>ClueList</c> 写一条 <c>PropositionInfo</c>
    /// （时间 / 人物 / 房间由命令指定，活动走未知字段 100），再广播 <c>S_ADD_CLUE</c>。
    /// 玩家扫描该设备时才会拿到它，所以「被搜到」的语义与真实线索完全一致。
    /// </para>
    /// <para>
    /// <b>代价</b>：执行成功后调用发信机自己的 <c>ChatDevice.RecordMessageClue(player, false)</c>
    /// （:165172 → <c>Device.RecordLastUsingPlayer</c> :166386），
    /// 于是原版会给操作者留一条「XX 在发信机发送了消息」的线索 —— 这就是暴露风险。
    /// </para>
    /// <para>
    /// <b>提示</b>：本局录入的伪证键（<c>玩家:时间:房间</c>）记在 <see cref="ClueFakeState"/>，
    /// 会议线索播报与 <c>/clue</c> 在末尾追加「有 N 条线索可能为伪造」。
    /// </para>
    /// <para>
    /// ⚠️ 子配置全部热读（<c>ConfigEntry.Value</c>）；只有会话与重写槽是运行期状态，
    /// 热关闭时由 <see cref="OnDisabled"/> 清掉。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueFake",
        "黑方/黑幕的局内能力：在发信机的聊天频道里用 /f 录入一条虚假线索（时间/人物/房间/活动），" +
        "写进目标房间设备的线索表，玩家扫描即可搜到；执行时会在该发信机留下「发送了消息」的线索作为代价。" +
        "会议播报与 /clue 会在末尾提示「有 N 条线索可能为伪造」。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal partial class ClueFakeFeature
    {
        /// <summary>每人（或每阵营）每局最多录入几次。</summary>
        public static ConfigEntry<int> MaxPerRound { get; private set; }

        /// <summary>配额口径：<c>player</c> = 每人，<c>team</c> = 黑 / 黑幕各一个池。</summary>
        public static ConfigEntry<string> QuotaMode { get; private set; }

        /// <summary>会议线索播报末尾是否提示「可能为伪造」。</summary>
        public static ConfigEntry<bool> ReportHint { get; private set; }

        /// <summary><c>x</c> 重写槽的存活时间（秒）。</summary>
        public static ConfigEntry<int> RewriteTimeoutSeconds { get; private set; }

        /// <summary>黑方（<c>EPlayerColor.Black</c>）是否允许使用 <c>/f</c>；黑幕不受影响。</summary>
        public static ConfigEntry<bool> BlackEnabled { get; private set; }

        /// <summary>x 模式的排队策略：<c>overwrite</c> = 覆盖；<c>queue</c> = 排队。</summary>
        public static ConfigEntry<string> XMode { get; private set; }

        /// <summary>当前是否队列模式。</summary>
        internal static bool QueueXMode
        {
            get
            {
                string mode = XMode == null ? "overwrite" : (XMode.Value ?? "overwrite").Trim();
                return mode.Equals("queue", global::System.StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>配额是否按阵营而不是按人计算。</summary>
        internal static bool TeamQuota
        {
            get
            {
                string mode = QuotaMode == null ? "player" : (QuotaMode.Value ?? "player").Trim();
                return mode.Equals("team", global::System.StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>每局上限（配置脏值时退回 1）。</summary>
        internal static int MaxPerRoundValue
        {
            get { return MaxPerRound == null ? 1 : MaxPerRound.Value; }
        }

        public static void Bind(ConfigFile config, string section)
        {
            MaxPerRound = config.Bind(
                section,
                "MaxPerRound",
                1,
                new ConfigDescription(
                    "每个玩家（或每个阵营）每局最多录入几条虚假线索。\n" +
                    "· QuotaMode = player（默认）= 每个黑方 / 黑幕玩家各自计算；\n" +
                    "· QuotaMode = team = 黑方一个池、黑幕一个池，各自共享。\n" +
                    "回大厅时清零（线索不跨局）。",
                    new AcceptableValueRange<int>(0, 10)));

            QuotaMode = config.Bind(
                section,
                "QuotaMode",
                "player",
                "配额口径：player = 每人每局；team = 黑方 / 黑幕各一个共享池。\n" +
                "只影响次数计算，不影响谁能用。");

            BlackEnabled = config.Bind(
                section,
                "BlackEnabled",
                true,
                "黑方（Black）是否允许使用 /f。\n" +
                "关闭后只有黑方不能用；黑幕（Dark / Mastermind）不受影响。\n" +
                "热读，改 .cfg 立即生效。");

            XMode = config.Bind(
                section,
                "XMode",
                "overwrite",
                new ConfigDescription(
                    "x 模式的排队策略：\n" +
                    "· overwrite = 覆盖：连续执行两次只保留最后一次（默认）；\n" +
                    "· queue = 排队：按顺序逐条改写/标记。\n" +
                    "热读，改 .cfg 立即生效。",
                    new AcceptableValueList<string>("overwrite", "queue")));

            ReportHint = config.Bind(
                section,
                "ReportHint",
                true,
                "会议开始时的个人线索播报，末尾是否追加「在当前所搜集到的全部线索中，有 N 条可能为伪造」。\n" +
                "只报数量、不标是哪一条。这是唯一的「可能为伪造」入口（/clue 不再重复提示）。");

            RewriteTimeoutSeconds = config.Bind(
                section,
                "RewriteTimeoutSeconds",
                300,
                new ConfigDescription(
                    "/f 的 x 模式（重写下一条真实线索）在多长时间内有效（秒）。\n" +
                    "到期未命中下一条线索就作废；只影响一次记录。",
                    new AcceptableValueRange<int>(0, 3600)));
        }

        /// <summary>热关闭：清掉会话与未应用的重写槽（已录入的伪证留在设备表里，不撤销）。</summary>
        public static void OnDisabled()
        {
            ClueFakeState.ClearSessions();
        }
    }
}
