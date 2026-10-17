using ClueDetail.Core;
using HarmonyLib;
using Protocol;
using Server.Game;

namespace ClueDetail.Features.Host.SecondVote
{
    internal static partial class SecondVoteFeature
    {
        /// <summary>
        /// 审判阶段切换的唯一收口（<c>State</c> 的 setter 内部就是 <c>ChangeTrialState</c>，:181888）。
        /// </summary>
        /// <remarks>
        /// 插入点选在这里而不是 <c>StartVoteResult</c>：后者的目标状态算在**局部变量**里，补丁够不着；
        /// 而 setter 是每一次阶段变化都必经的一步，改 <c>value</c> 就等于把这一跳改成另一跳。
        /// <list type="bullet">
        /// <item>`Discuss` —— 新一轮审判开始，清掉"已触发过二次投票"的标记；</item>
        /// <item>`Replay` / `TrialResult` —— 投票已经算完，此时判断是否该改道回投票。</item>
        /// </list>
        /// 第二轮本身不会再经过 `Discuss`（我们直接把它送进 `VotePhase`），所以标记能正确保持。
        /// </remarks>
        [HarmonyPatch(typeof(TrialManager), "set_State")]
        internal static class StateHook
        {
            private static void Prefix(TrialManager __instance, ref ETrialState value)
            {
                if (value == ETrialState.Discuss)
                {
                    SecondVoteRule.Reset();
                    return;
                }

                if (value != ETrialState.Replay && value != ETrialState.TrialResult)
                {
                    return;
                }

                if (SecondVoteRule.TryRearm(__instance))
                {
                    // 改道：不进回放/终局结算，直接再投一轮。
                    value = ETrialState.VotePhase;
                }
            }
        }

        /// <summary>
        /// 第二轮拒收投给黑幕的票。
        /// </summary>
        /// <remarks>
        /// 装了本模块的客户端在界面上已经点不到黑幕（那条规定来自 <c>S_SEND_TRIAL_EVENT</c> 的
        /// <c>IsAlive</c>）；未装本模块的客户端仍然点得到，所以服务端这一层是**必要的兜底** ——
        /// 否则"不可投"就只是视觉效果。
        /// <para>
        /// 返回 false 表示整段跳过原方法：票不计入、也不广播 <c>S_VOTE_TRIAL</c>，
        /// 对投票者而言表现成"点了没反应"，与原版行为一致（原版对无效目标也是静默忽略）。
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(TrialManager), "VotePlayer")]
        internal static class VoteHook
        {
            private static bool Prefix(int voteId)
            {
                if (!SecondVoteRule.IsBanned(voteId))
                {
                    return true;
                }

                global::ClueDetail.Core.Log.Info($"第二轮：拒绝投给黑幕（玩家 {voteId}）的票。");
                return false;
            }
        }
    }
}
