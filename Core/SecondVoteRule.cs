using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Protocol;
using GamePlayer = Server.Game.Player;
using Server.Game;

namespace ClueDetail.Core
{
    /// <summary>
    /// 「黑幕二次投票」：第一次投票若投出黑幕，不作终局结算，而是立即重开一轮投票。
    /// </summary>
    /// <remarks>
    /// <b>规则</b>（用户 2026-10-18 定）：当且仅当**第一次**投票的最高票是黑幕时，
    /// 进入第二次投票，且第二轮**不可投黑幕**；其余情况一律与原版一致。
    /// 第二轮不再有讨论阶段（直接投票）；任意一轮投出黑方或白方都按原版胜利条件结算；
    /// 平票 / 无人投票 / 投出普通人 —— 与原版相同，**黑方获胜**。
    /// <para>
    /// <b>为什么能纯服务端做</b>：
    /// <list type="bullet">
    /// <item>投票界面的"谁能被投"完全由服务端下发的 <c>S_SEND_TRIAL_EVENT.PlayerInfos</c>
    /// （<c>repeated TrialInfo</c>）驱动 —— 客户端 <c>UI_TrialEvent.SetInfo</c> 拿它喂给
    /// <c>UI_TrialPortraitSubItem.SetInfo(_, info.IsAlive, …)</c>，点选时校验的正是这个
    /// <c>IsAlive</c>（0.1.18 :71981 / :73774）；</item>
    /// <item>阶段切换也由服务端 <c>TrialManager.State</c> 的 setter 广播
    /// <c>S_TRIAL_STATE</c> 决定（:181888）；</item>
    /// <item>客户端 <c>StartVotePhase()</c> 进来就把 <c>_hasVoted</c> 清掉（:73054），
    /// 所以重开一轮不会卡在"已投过"。</item>
    /// </list>
    /// </para>
    /// <para>
    /// ⚠️ <b>副作用（已知并接受）</b>：客户端收到 <c>IsAlive = false</c> 会执行
    /// <c>KnownDeadIds.Add(playerId)</c>（:71977），连带把黑幕在**本轮审判**里当作死者 ——
    /// 语音被静音（:70861）、聊天进死者频道（:42820）、绘画面板列为死者（:54629）。
    /// 该列表每次审判 <c>SetInfo</c> 会先清空（:71945），影响局限于审判阶段；
    /// 且原版处刑走的就是同一套（<c>MarkExecutedAsDead</c> :72629）。
    /// 我们**不**改动 <c>Player.IsAlive</c>，所以游戏逻辑与结算不受影响。
    /// </para>
    /// </remarks>
    internal static class SecondVoteRule
    {
        private static readonly FieldInfo CandidatesField =
            AccessTools.Field(typeof(TrialManager), "_candidates");

        private static readonly MethodInfo MostVotedMethod =
            AccessTools.Method(typeof(TrialManager), "MostVotedPlayerId");

        /// <summary>本局是否已经触发过第二轮（每次审判重置）。</summary>
        private static bool _fired;

        /// <summary>第二轮生效中：此时服务端拒收投给黑幕的票。</summary>
        private static int _bannedPlayerId;

        public static void Reset()
        {
            _fired = false;
            _bannedPlayerId = 0;
        }

        /// <summary>第二轮是否已生效（用于 <c>VotePlayer</c> 拒收）。</summary>
        public static bool IsBanned(int playerId)
        {
            return playerId != 0 && playerId == _bannedPlayerId;
        }

        /// <summary>
        /// 判断当前这一轮是否应当改道回投票（即触发第二轮）。返回 true 表示调用方应把
        /// 目标状态由 Replay/TrialResult 改成 VotePhase。
        /// </summary>
        public static bool TryRearm(TrialManager manager)
        {
            if (_fired || manager == null)
            {
                return false;
            }

            GameRoom room = GameRoom.Instance;
            GamePlayer mastermind = room?.MasterMind;
            if (mastermind == null)
            {
                return false;
            }

            int voted = MostVoted(manager);
            int mastermindId = mastermind.PublicInfo.PlayerId;
            if (voted == 0 || voted != mastermindId)
            {
                // 平票 / 无人投票 / 投出黑方 / 投出其他人 —— 全部按原版走。
                return false;
            }

            _fired = true;
            _bannedPlayerId = mastermindId;

            ResetBallot(manager);
            MarkUnvotable(room, mastermindId);

            Log.Info($"第一次投票投出黑幕（玩家 {mastermindId}）—— 不结算，立即进入第二轮投票；本轮黑幕不可被投。");
            return true;
        }

        /// <summary>取本轮最高票者（原版口径：平票或无人投票返回 -1）。</summary>
        private static int MostVoted(TrialManager manager)
        {
            try
            {
                object result = MostVotedMethod?.Invoke(manager, null);
                return result is int id ? id : 0;
            }
            catch (System.Exception ex)
            {
                Log.Exception(ex, "读取本轮最高票者失败，按原版流程继续");
                return 0;
            }
        }

        /// <summary>
        /// 清空票箱（票数、已投标记、目标），让第二轮从零开始。
        /// </summary>
        /// <remarks>
        /// 原版只有 <c>TrialManager.Init</c>（:182106）会重建 <c>_candidates</c>，而那是**审判开始时**
        /// 才跑的 —— 第二轮沿用同一个列表，所以必须自己把票清掉，否则两轮票数会叠加。
        /// 这里**不**动 <c>IsAlive</c> 与候选人构成：黑幕仍留在名单里（原版
        /// <c>MostVotedPlayerId</c> 有 <c>_candidates.Count &gt; 1</c> 的断言），
        /// 只是服务端会拒收投给他的票。
        /// </remarks>
        private static void ResetBallot(TrialManager manager)
        {
            try
            {
                if (CandidatesField?.GetValue(manager) is IEnumerable candidates)
                {
                    foreach (object item in candidates)
                    {
                        if (item is TrialManager.Candidate candidate)
                        {
                            candidate.Info.VoteCount = 0;
                            candidate.Vote = false;
                            candidate.VotedTargetId = -1;
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                Log.Exception(ex, "清空票箱失败 —— 第二轮可能带上第一轮的票数");
            }
        }

        /// <summary>
        /// 把黑幕标为"本轮不可投"，并让客户端投票界面跟着变。
        /// </summary>
        /// <remarks>
        /// 客户端那份 <c>IsAlive</c> 只能来自 <c>S_SEND_TRIAL_EVENT</c>，所以这里自己构造一条发出去
        /// （0.1.18 <c>StartTrial</c> :174640 的同类包就是这么组的）。
        /// 关键：**只改这个包里的值，不改 <c>Player.IsAlive</c>** —— 因此不影响任何游戏逻辑与结算。
        /// </remarks>
        private static void MarkUnvotable(GameRoom room, int mastermindId)
        {
            try
            {
                var packet = new S_SEND_TRIAL_EVENT();
                foreach (GamePlayer player in room.Players)
                {
                    if (player == null || player.IsSpectator)
                    {
                        continue;
                    }

                    int playerId = player.PublicInfo.PlayerId;
                    packet.PlayerInfos.Add(new TrialInfo
                    {
                        PlayerId = playerId,
                        // 黑幕报 false ⇒ 客户端投票界面不给他投票按钮（本轮限定）
                        IsAlive = playerId != mastermindId && player.IsAlive,
                    });
                }

                room.Broadcast(packet);
            }
            catch (System.Exception ex)
            {
                Log.Exception(ex, "下发\"黑幕不可投\"的审判名单失败 —— 客户端仍可点他，但服务端会拒收");
            }
        }
    }
}
