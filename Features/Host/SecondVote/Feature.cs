using ClueDetail.Core;

namespace ClueDetail.Features.Host.SecondVote
{
    /// <summary>
    /// 黑幕二次投票：第一次投票若投出黑幕，不结算，立即重开一轮投票（本轮不可投黑幕）。
    /// </summary>
    /// <remarks>
    /// 规则（2026-10-18 定）：
    /// <list type="bullet">
    /// <item>**当且仅当第一次投票**的最高票是黑幕 → 进入第二次投票，且第二轮不可投黑幕；</item>
    /// <item>其余情况（平票 / 无人投票 / 投出黑方 / 投出普通人）**均不触发**二次投票；</item>
    /// <item>任意一轮投出黑方 → 白方胜利；投出黑幕 → 黑方胜利（黑幕本人不算赢，沿用原版
    /// <c>MastermindVoteLoss</c> 口径）；平票 / 无人投票 → **黑方胜利**（与原版一致）。</item>
    /// </list>
    /// <para>
    /// 第二轮**不重开讨论**，直接进投票；客户端重复进入 <c>VotePhase</c> 时会把"已投票"标记
    /// 清掉（0.1.18 :73054），所以不需要额外处理。
    /// </para>
    /// <para>纯服务端实现，未装本模块的客户端也能正常参与（只是点黑幕时会被服务端拒收）。</para>
    /// </remarks>
    [PatchFeature(
        "ClueSecondVote",
        "黑幕二次投票：第一次投票若投出黑幕，则不作终局结算，把黑幕标为本轮不可投并立即重开一轮投票（不重开讨论）。第二轮按原版规则结算：投出黑方则白方胜，其余（含平票、无人投票、投出普通人）黑方胜。其余情况一律与原版一致。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static partial class SecondVoteFeature
    {
        /// <summary>每次加载清零状态（真正的重置在每轮审判开始时的 <c>Discuss</c> 上）。</summary>
        public static void OnLoaded()
        {
            SecondVoteRule.Reset();
        }
    }
}
