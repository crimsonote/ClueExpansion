using ClueDetail.Core;
using HarmonyLib;
using Protocol;
using GamePlayer = Server.Game.Player;
using ServerDevice = Server.Game.Device;
using ServerGameRoom = Server.Game.GameRoom;
using ServerTimeManager = Server.Game.TimeManager;

namespace ClueDetail.Features.Host.ClueMerge
{
    internal partial class ClueMergeFeature
    {
        /// <summary>
        /// 线索写入时的合并窗口：把原版写死的 30 秒桶换成配置值。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>只在配置不是 30 时接管</b>（30 = 原版 ⇒ 直接放行）。接管时逐字复刻 0.1.18
        /// <c>Device.RecordLastUsingPlayer</c>（:166386-166440），包括：
        /// 阶段守卫、<c>WhatType != WNone</c>、身份映射（<c>Anonymity→0 / Mastermind→11037 / Black→66613</c>）、
        /// 合并分支的 <c>S_REMOVE_CLUE</c>（旧副本）→ 改 <c>Time</c> → <c>S_ADD_CLUE</c>（新副本）顺序，
        /// 以及给触发者发 <c>S_LEFT_CLUE{RoleFace}</c>。
        /// </para>
        /// <para>
        /// ⚠ <c>[HarmonyPriority(Priority.Low)]</c>：要高优先级让**别人先跑**——
        /// 身份污染（<c>ShadowSabotage.IdentityPolluteHook</c>，<c>Priority.First</c>）先把 <c>state</c> 改成
        /// <c>Anonymity</c>，<c>RecordDetail.RecordHook</c>（默认优先级）再把真实活动记进旁路表，
        /// 最后才轮到本方法算桶。
        /// </para>
        /// <para>
        /// ⚠ 接管后 <c>RecordDetail</c> 与 <c>ShadowSabotage</c> 挂在该方法上的**两个 Postfix 不会跑**，
        /// 所以这里显式调用 <c>StampWhat</c>（字段 100）与 <c>StampPollution</c>（字段 101/103）；
        /// 顺序与原版一致：先写活动、再写污染标记。
        /// </para>
        /// <para>
        /// ⚠ 一旦决定接管，**无论成败都不再让原版跑**（返回 false）：否则"接管写了一半 + 原版再写一条"
        /// 会留下重复线索。异常只记日志。
        /// </para>
        /// </remarks>
        [HarmonyPriority(Priority.Low)]
        [HarmonyPatch(typeof(ServerDevice), nameof(ServerDevice.RecordLastUsingPlayer))]
        internal static class MergeWindowHook
        {
            private static bool Prefix(ServerDevice __instance, GamePlayer player, Define.EWhatState state)
            {
                if (!FeatureLoader.Enabled<ClueMergeFeature>()) return true;
                // ⚠️ 2026-10-20：**不再**在 Seconds == 30 时放行原版。
                // 原版合并判据只有 `PlayerId + 30 秒桶`，不看这条线索是否产生于污染期：
                //   · 破坏前（真实）的记录与污染期（匿名）的记录会被并成一条；
                //   · 污染期与修复后的记录会被并成一条，而且旧标记会永久粘在条目上。
                // 所以这里**总是接管**，由 Execute 额外做「污染标记相同才允许合并」。
                // Seconds == 30 时，桶长与阶段守卫都与原版逐字一致，差别只有这一条分组。
                if (__instance == null || player == null || __instance.DeviceData == null) return true;

                ServerGameRoom room = ServerGameRoom.Instance;
                ServerTimeManager tm = ServerTimeManager.Instance;
                if (room == null || tm == null || room.State != EGameState.Survive) return true;
                if (__instance.DeviceData.WhatType == EWhatType.WNone) return true;

                Execute(__instance, player, state, room, tm);
                return false;
            }

            /// <summary>复刻原版方法体，只把桶长换成配置值。</summary>
            private static void Execute(
                ServerDevice device,
                GamePlayer player,
                Define.EWhatState state,
                ServerGameRoom room,
                ServerTimeManager tm)
            {
                try
                {
                    // 真实操作者：103 写的就是它，SamePollution 必须拿同一个值去比，
                    // 不能用下面映射后的 playerId（那可能是 0 / 11037 / 66613）。
                    int realOwner = player.PublicInfo.PlayerId;
                    int playerId = player.PublicInfo.PlayerId;
                    switch (state)
                    {
                        case Define.EWhatState.Anonymity:
                            playerId = 0;
                            break;
                        case Define.EWhatState.Mastermind:
                            playerId = 11037;
                            break;
                        case Define.EWhatState.Black:
                            playerId = 66613;
                            break;
                    }

                    int surviveTime = tm.SurviveTime;
                    int limitTime = ClueMergeWindow.LimitTime(surviveTime);
                    var list = device.ClueList;
                    if (list == null)
                    {
                        return;
                    }

                    // 这次记录将要打上的标记（与 StampPollution 同口径）。
                    bool timeOn = global::ClueDetail.Features.Host.ShadowSabotage.ShadowSabotageFeature.TimePollutionOn;
                    bool identityOn = global::ClueDetail.Features.Host.ShadowSabotage.ShadowSabotageFeature.IdentifyPollutionOn;
                    int frozen = global::ClueDetail.Features.Host.ShadowSabotage.ShadowSabotageFeature.FrozenTime;

                    PropositionInfo exist = null;
                    int existIndex = -1;
                    PropositionInfo nearMiss = null;   // 同人同桶但污染标记不符的第一条（诊断用）
                    for (int i = 0; i < list.Count; i++)
                    {
                        PropositionInfo x = list[i];
                        if (ShouldMerge(x, limitTime, playerId, timeOn, frozen, identityOn, realOwner))
                        {
                            exist = x;
                            existIndex = i;
                            break;
                        }

                        // 同人同桶但「污染标记」不符 ⇒ 记下来做诊断（日志会打它的 101/103）。
                        if (nearMiss == null && x != null
                            && x.Time >= limitTime && x.PlayerId == playerId && !x.Destroyed)
                        {
                            nearMiss = x;
                        }
                    }

                    PropositionInfo stamped = null;

                    if (exist == null)
                    {
                        var fresh = new PropositionInfo
                        {
                            PlayerId = playerId,
                            Time = surviveTime,
                            RoomId = device.RoomID,
                        };
                        list.Add(fresh);
                        room.Broadcast(new S_ADD_CLUE { DeviceId = device.ID, Info = fresh.Clone() });
                        stamped = fresh;
                    }
                    else
                    {
                        // 与原版一致：先撤旧副本、再发新副本（客户端身份判定含 Time，必须成对发）。
                        PropositionInfo old = exist.Clone();
                        // ⚠️ 必须按索引删：PropositionInfo 的 Equals 是**按字段值**比较的，
                        // list.Remove(exist) 会在存在同值记录时删掉第一个（不是它自己），
                        // 随后 list.Add 又加回 ⇒ 每合并一次数量 +1（实机 3 条就是这么来的）。
                        list.RemoveAt(existIndex);
                        exist.Time = surviveTime;
                        list.Add(exist);
                        room.Broadcast(new S_REMOVE_CLUE { DeviceId = device.ID, Info = old });
                        room.Broadcast(new S_ADD_CLUE { DeviceId = device.ID, Info = exist.Clone() });
                        stamped = exist;
                    }

                    player.Session.Send(new S_LEFT_CLUE
                    {
                        RoleFace = state == Define.EWhatState.Mastermind || state == Define.EWhatState.Black,
                    });

                    // 接管后两个 Postfix 不会跑 —— 这里补上它们的职责（顺序与原版一致）。
                    global::ClueDetail.Features.Host.RecordDetail.RecordDetailFeature.RecordHook.StampWhat(
                        device, player, state);

                    // ⚠️ 2026-10-20：直接用**对象引用**打标，不再调 StampPollution 按时间反查
                    // （两次读 SurviveTime 之间跨秒边界会漏，实机症状就是"线索没有冻结时间"）。
                    global::ClueDetail.Features.Host.ShadowSabotage.ShadowSabotageFeature.StampOne(
                        stamped, player.PublicInfo.PlayerId, timeOn, identityOn, frozen);

                    // 未命中候选的诊断文本先算出来：内插字符串里再嵌 `? :` + 多个字符串字面量，
                    // 编译器会解析失败（CS1519），所以拆成局部变量。
                    string missText = nearMiss == null
                        ? "无"
                        : "#" + nearMiss.Time
                          + " 101=" + ClueDetail.Core.UnknownFields.GetDisplayTime(nearMiss)
                          + " 103=" + ClueDetail.Core.UnknownFields.GetRealOwner(nearMiss);

                    global::ClueDetail.Core.Log.Info(
                        $"线索合并窗口：桶长 {ClueMergeWindow.Seconds} 秒（0 = 禁用）" +
                        $"→ {(exist == null ? "新建" : "合并")} [{playerId} / {surviveTime} 秒 / 房间 {device.RoomID}]" +
                        $"（污染 时间={(timeOn ? frozen.ToString() : "无")} /" +
                        $" 身份={(identityOn ? realOwner.ToString() : "无")}" +
                        $" ｜ 未命中={missText}）。");
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "线索合并窗口：接管失败（本条线索可能不完整）。");
                }
            }

            /// <summary>
            /// 合并判据（**纯函数**，不碰任何游戏单例）。
            /// </summary>
            /// <remarks>
            /// <para>
            /// 抽出来是为了能离线测试：<c>Execute</c> 一进门就要 <c>GameRoom.Instance</c> /
            /// <c>TimeManager.Instance</c>，离线全是 null，整段跑不起来；
            /// 而判据本身只依赖入参，`.tmps\clue-test` 用真实 DLL 反射就能覆盖全部组合。
            /// </para>
            /// <para>判据 = 原版三条 + <see cref="SamePollution"/> 一条。</para>
            /// </remarks>
            internal static bool ShouldMerge(
                PropositionInfo x, int limitTime, int playerId,
                bool timeOn, int frozen, bool identityOn, int owner)
            {
                return x != null
                    && x.Time >= limitTime
                    && x.PlayerId == playerId
                    && !x.Destroyed
                    && SamePollution(x, timeOn, frozen, identityOn, owner);
            }

            /// <summary>
            /// 两条线索的「污染标记」是否一致 —— 合并分组的唯一额外条件（2026-10-20 新增）。
            /// </summary>
            /// <remarks>
            /// 只有标记完全相同的两条才允许合并：
            /// <list type="bullet">
            /// <item>都产生于时钟破坏期（字段 101 == 当前冻结值），或者都不在污染期；</item>
            /// <item>都产生于身份破坏期（字段 103 == 真实当事人），或者都不在污染期。</item>
            /// </list>
            /// 于是「破坏前 → 污染期」「污染期 → 修复后」都不会互相覆盖，
            /// 而同一个污染期内的多次使用仍按原版语义合并。
            /// </remarks>
            private static bool SamePollution(
                PropositionInfo x, bool timeOn, int frozen, bool identityOn, int owner)
            {
                int display = UnknownFields.GetDisplayTime(x);
                int realOwner = UnknownFields.GetRealOwner(x);

                bool timeOk = timeOn ? display == frozen : display == 0;
                bool identityOk = identityOn ? realOwner == owner : realOwner == 0;
                return timeOk && identityOk;
            }
        }
    }
}