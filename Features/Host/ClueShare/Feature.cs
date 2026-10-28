using BepInEx.Configuration;
using ClueDetail.Core;

namespace ClueDetail.Features.Host.ClueShare
{
    /// <summary>
    /// 调查阶段黑 / 黑幕的线索互通（实验性）。
    /// </summary>
    /// <remarks>
    /// 两级：1 = 只互通开局自动提供的线索；2 = 搜索获得的线索也互通。
    /// 冲突交给客户端判据（与 <c>AlreadyExistClue</c> 同口径），服务端不删条目。
    /// 尸体与刀架线索**不同步**（平板/刀架提示可能暴露身份）。
    /// </remarks>
    [PatchFeature(
        "ClueShare",
        "调查阶段让黑与黑幕互通线索（1=仅开局线索，2=搜索线索也互通；尸体与刀架不同步）。",
        defaultEnabled: false,
        side: FeatureSide.Host)]
    internal partial class ClueShareFeature
    {
        private const string Section = "ClueShare";

        /// <summary>1 = 只同步开局线索；2 = 搜索线索也同步。</summary>
        public static ConfigEntry<int> Tier { get; private set; }

        /// <summary>同 (房间, 时刻) 的匿名线索全部解密后，剔除旧的匿名条目。</summary>
        public static ConfigEntry<bool> DedupSamePlace { get; private set; }

        public static void Bind(ConfigFile config, string section)
        {
            Tier = config.Bind(
                section,
                "Tier",
                1,
                "1 = 只互通开局自动提供的线索；2 = 黑/黑幕各自搜索到的线索也互通。\n" +
                "尸体与刀架线索始终不同步。");

            DedupSamePlace = config.Bind(
                section,
                "DedupSamePlace",
                true,
                "同 (房间, 时刻) 的「某人」线索全部解密后，剔除旧的匿名条目（S_REMOVE_CLUE）。\n" +
                "false = 不做任何去重，两条都保留。");
        }

        internal static int TierValue => Tier != null ? Tier.Value : 1;

        internal static bool DedupValue => DedupSamePlace == null || DedupSamePlace.Value;
    }
}
