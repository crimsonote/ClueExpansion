using BepInEx.Configuration;
using ClueDetail.Core;

namespace ClueDetail.Features.Host.Meltdown
{
    /// <summary>
    /// 房主侧：断电后「融毁」倒计时的流逝速度，以及基于近期融毁胜率的停钟平衡。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 原版断电不会加速倒计时；但断电后白方做不了任务，等效于额外损失可用时间。
    /// 本功能允许把「断电期间的剩余时间流逝」调快 / 调慢，作为平衡手段。
    /// </para>
    /// <para>
    /// 另有一条自适应规则：统计最近 <see cref="RecentRounds"/> 局里「融毁胜利」
    /// （时间流空导致 <c>GameRoom.SurvivalTick</c> 走到 <c>GameOver</c>）的比例；
    /// 比例<b>大于</b> <see cref="RatioThreshold"/> 时，本局断电期间<b>停钟</b>（倍率 0），
    /// 并在开局第 <see cref="NoticeSecond"/> 秒或出现「白转黑」后，通过秘密通道告知黑方。
    /// </para>
    /// <para>
    /// ⚠️ 捉迷藏（HideAndSeek）启用时本功能默认失效：两边都会碰断电 / 时间，
    /// 避免两套规则互相打架（<see cref="DisableWithHideAndSeek"/> 可关掉这个保护）。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueMeltdown",
        "断电后剩余时间的流逝速度（可更快/更慢，或本局停钟）；若最近若干局融毁胜利（时间流空）比例过高，" +
        "本局断电时停钟，并在开局/白转黑后通过秘密通道告知黑方。捉迷藏启用时默认失效。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal partial class MeltdownFeature
    {
        /// <summary>断电后剩余时间的流逝倍率（比例未超阈值时）。1 = 原版；0 = 停钟。</summary>
        public static ConfigEntry<float> SpeedMultiplier { get; private set; }

        /// <summary>比例超阈值时的流逝倍率；0 = 完全停钟。</summary>
        public static ConfigEntry<float> PauseMultiplier { get; private set; }

        /// <summary>近期融毁胜利比例超阈值时，本局断电停钟。</summary>
        public static ConfigEntry<bool> PauseWhenOverRatio { get; private set; }

        /// <summary>统计最近多少局。</summary>
        public static ConfigEntry<int> RecentRounds { get; private set; }

        /// <summary>比例阈值（严格大于才触发）。</summary>
        public static ConfigEntry<float> RatioThreshold { get; private set; }

        /// <summary>开局第几秒告知黑方（0 = 不告知）。</summary>
        public static ConfigEntry<int> NoticeSecond { get; private set; }

        /// <summary>捉迷藏模式适配：auto = 模式启用时失效；on = 强制启用；off = 强制失效。</summary>
        public static ConfigEntry<string> HideAndSeekMode { get; private set; }

        /// <summary>是否发送开局/白转黑提示。</summary>
        public static ConfigEntry<bool> NoticeEnabled { get; private set; }

        /// <summary>提示文案，{ratio} 会替换成阈值百分比。</summary>
        public static ConfigEntry<string> NoticeText { get; private set; }

        public static void Bind(ConfigFile config, string section)
        {
            SpeedMultiplier = config.Bind(
                section,
                "SpeedMultiplier",
                1f,
                new ConfigDescription(
                    "断电后剩余时间的流逝倍率：\n" +
                    "· 1 = 原版（每秒扣 1 秒）；\n" +
                    "· 2 = 每秒扣 2 秒（更快融毁）；\n" +
                    "· 0.5 = 每秒扣 0.5 秒（更慢）；\n" +
                    "· 0 = 停钟。\n" +
                    "只在断电（2 台电闸被拉断）期间生效；恢复供电后恢复正常。",
                    new AcceptableValueRange<float>(0f, 5f)));

            PauseMultiplier = config.Bind(
                section,
                "PauseMultiplier",
                0f,
                new ConfigDescription(
                    "最近 N 局融毁比例超阈值（本局「停钟状态」）时，断电期间剩余时间的流逝倍率：\n" +
                    "· 0 = 完全停钟（默认）；\n" +
                    "· 0.2 = 大幅度降低（每秒扣 0.2 秒）；\n" +
                    "· 1 = 与原版一致。\n" +
                    "解除停钟需要**所有**电闸恢复供电（不是修好一台就恢复）。",
                    new AcceptableValueRange<float>(0f, 5f)));

            PauseWhenOverRatio = config.Bind(
                section,
                "PauseWhenOverRatio",
                true,
                "最近若干局里「融毁胜利」（时间流空）的比例大于阈值时，本局断电期间停钟。\n" +
                "比例按最近 RecentRounds 局统计，跨重启保留。");

            RecentRounds = config.Bind(
                section,
                "RecentRounds",
                10,
                new ConfigDescription(
                    "统计最近多少局（跨重启保留）。",
                    new AcceptableValueRange<int>(1, 50)));

            RatioThreshold = config.Bind(
                section,
                "RatioThreshold",
                0.3f,
                new ConfigDescription(
                    "融毁胜利比例阈值（0~1）。比例**严格大于**该值时停钟。",
                    new AcceptableValueRange<float>(0f, 1f)));

            NoticeSecond = config.Bind(
                section,
                "NoticeSecond",
                5,
                new ConfigDescription(
                    "开局第几秒通过秘密通道告知黑方「本局断电时倒计时流逝将会暂停」。0 = 不主动告知。",
                    new AcceptableValueRange<int>(0, 60)));

            HideAndSeekMode = config.Bind(
                section,
                "HideAndSeekMode",
                "auto",
                new ConfigDescription(
                    "捉迷藏模式适配：\n" +
                    "· auto = 捉迷藏模式（HideAndSeek 的 [HS_Mode] Enabled）启用时本功能失效（默认）；\n" +
                    "· on = 强制启用（即使捉迷藏模式开着也生效）；\n" +
                    "· off = 强制失效。",
                    new AcceptableValueList<string>("auto", "on", "off")));

            NoticeEnabled = config.Bind(
                section,
                "NoticeEnabled",
                true,
                "是否发送「本局断电时倒计时流逝将会暂停」的开局 / 白转黑提示。");

            NoticeText = config.Bind(
                section,
                "NoticeText",
                "【断电融毁】近期融毁胜利比例大于{ratio}%，本局断电时倒计时流逝将会暂停。",
                "提示文案。{ratio} 会替换为阈值百分比（如 30）；留空则用默认文案。");
        }

        public static void OnDisabled()
        {
            MeltdownState.Reset();
        }
    }
}
