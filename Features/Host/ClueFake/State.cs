using System.Collections.Generic;
using Protocol;
using ServerGamePlayer = Server.Game.Player;
using ServerGameRoom = Server.Game.GameRoom;

namespace ClueDetail.Features.Host.ClueFake
{
    /// <summary>
    /// 线索伪造的运行期状态（纯内存，全部在主线程读写；<see cref="HasSession"/> 会被
    /// 聊天包 Prefix 的线程读到，所以用 <see cref="Gate"/> 保护）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 三份数据：
    /// <list type="bullet">
    /// <item><b>会话</b>：交互式问答的进度（每个玩家一份，超时/换阶段/exit 清理）；</item>
    /// <item><b>配额</b>：本局已用次数 —— 按玩家或按阵营（黑 / 黑幕各一个池）；</item>
    /// <item><b>伪证键</b>：本局录入过的虚假线索（<c>玩家:时间:房间</c>），
    /// 供 <c>/clue</c> 与会议播报统计「可能为伪造」的条数。</item>
    /// </list>
    /// </para>
    /// <para>
    /// ⚠️ 全部是**本局**状态：回大厅（<c>EGameState.Lobby</c>）一律清空，线索不跨局
    /// （与 <c>ClueIndex.Clear</c> 同口径）。
    /// </para>
    /// </remarks>
    internal static class ClueFakeState
    {
        /// <summary>交互式问答的当前步骤。</summary>
        internal enum Step
        {
            Time = 1,
            Person = 2,
            Room = 3,
            Activity = 4,
        }

        /// <summary>一个玩家的录入会话。</summary>
        internal sealed class Session
        {
            public int Owner;

            public Step Step = Step.Time;

            public bool HasTime;
            public int Time;

            public bool HasPerson;
            public int PersonId;

            public bool HasRoom;
            public int RoomId;

            public bool HasActivity;
            public int What;

            /// <summary>时间是否用偏移值（<c>+25</c> / <c>-15</c>，单位分钟）。</summary>
            public bool HasTimeOffset;
            public int TimeOffset;

            // ── 逐字段「x = 真实」标记（任一为 true 即进入「改写模式」）──
            public bool RealTime;
            public bool RealPerson;
            public bool RealRoom;
            public bool RealActivity;

            // ── 逐字段「显式未提供（- 或非法）」，需要交互式补全 ──
            public bool DashTime;
            public bool DashPerson;
            public bool DashRoom;
            public bool DashActivity;

            /// <summary>最后一次交互时刻（<c>Time.realtimeSinceStartup</c>），用于超时清理。</summary>
            public float LastAt;

            /// <summary>任一字段用了 <c>x</c> ⇒ 改写模式（等下一条线索，而不是现在写伪证）。</summary>
            public bool RewriteMode
            {
                get { return RealTime || RealPerson || RealRoom || RealActivity; }
            }

            /// <summary>
            /// 改写模式下「缺省 = 真实」：把既没给值、也没显式 <c>-</c> 的字段补成真实。
            /// 这才能在 <c>/f x x x</c>（活动省略）时得到「完全原样保留」。
            /// </summary>
            public void FinalizeRewriteDefaults()
            {
                if (!RewriteMode)
                {
                    return;
                }

                if (!HasTime && !DashTime) { RealTime = true; }
                if (!HasPerson && !DashPerson) { RealPerson = true; }
                if (!HasRoom && !DashRoom) { RealRoom = true; }
                if (!HasActivity && !DashActivity) { RealActivity = true; }
            }

            public bool Complete
            {
                get
                {
                    if (RewriteMode)
                    {
                        return (RealTime || HasTime)
                            && (RealPerson || HasPerson)
                            && (RealRoom || HasRoom)
                            && (RealActivity || HasActivity);
                    }

                    return HasTime && HasPerson && HasRoom && HasActivity;
                }
            }

            public void Clear()
            {
                HasTime = false;
                HasPerson = false;
                HasRoom = false;
                HasActivity = false;
                HasTimeOffset = false;
                TimeOffset = 0;
                RealTime = false;
                RealPerson = false;
                RealRoom = false;
                RealActivity = false;
                DashTime = false;
                DashPerson = false;
                DashRoom = false;
                DashActivity = false;
                Step = Step.Time;
            }
        }

        /// <summary>待应用的「改写下一个线索」槽：逐字段控制（真实 / 改为指定值）。</summary>
        internal sealed class RewriteSlot
        {
            public bool Armed;

            /// <summary>所有字段都是真实值（<c>/f x x x</c>）：不改内容，只标记为伪造。</summary>
            public bool MarkOnly;

            /// <summary>发起者玩家 id：只改写**他自己**的下一条线索，别人的记录不碰。</summary>
            public int OwnerId;

            public bool RealTime;
            public bool RealPerson;
            public bool RealRoom;
            public bool RealActivity;

            public bool HasTimeOffset;
            public int TimeOffset;

            public int Time;
            public int PersonId;
            public int RoomId;

            public float ExpireAt;

            public void CopyFrom(RewriteSlot other)
            {
                if (other == null)
                {
                    Clear();
                    return;
                }

                Armed = other.Armed;
                MarkOnly = other.MarkOnly;
                OwnerId = other.OwnerId;
                RealTime = other.RealTime;
                RealPerson = other.RealPerson;
                RealRoom = other.RealRoom;
                RealActivity = other.RealActivity;
                HasTimeOffset = other.HasTimeOffset;
                TimeOffset = other.TimeOffset;
                Time = other.Time;
                PersonId = other.PersonId;
                RoomId = other.RoomId;
                ExpireAt = other.ExpireAt;
            }

            public void Clear()
            {
                Armed = false;
                MarkOnly = false;
                HasTimeOffset = false;
                TimeOffset = 0;
                RealTime = false;
                RealPerson = false;
                RealRoom = false;
                RealActivity = false;
                Time = 0;
                PersonId = 0;
                RoomId = 0;
                ExpireAt = 0f;
            }
        }

        private static readonly object Gate = new object();

        private static readonly Dictionary<int, Session> Sessions = new Dictionary<int, Session>();

        private static readonly HashSet<string> FakeKeys = new HashSet<string>();

        /// <summary>按玩家计的已用次数（<c>QuotaMode = player</c>）。</summary>
        private static readonly Dictionary<int, int> UsedByPlayer = new Dictionary<int, int>();

        /// <summary>按阵营计的已用次数（<c>QuotaMode = team</c>）。键见 <see cref="TeamKey"/>。</summary>
        private static readonly Dictionary<int, int> UsedByTeam = new Dictionary<int, int>();

        internal static readonly RewriteSlot Rewrite = new RewriteSlot();

        /// <summary>待应用的 x 队列（仅 <c>XMode = queue</c> 时使用）。</summary>
        private static readonly Queue<RewriteSlot> RewriteQueue = new Queue<RewriteSlot>();

        /// <summary>
        /// 挂起一个改写槽。覆盖模式：清空队列，直接替换当前槽；队列模式：当前槽已占用则排队。
        /// </summary>
        internal static void ArmRewrite(RewriteSlot slot, bool queueMode)
        {
            lock (Gate)
            {
                if (!queueMode || !Rewrite.Armed)
                {
                    Rewrite.CopyFrom(slot);
                    Rewrite.Armed = true;
                    RewriteQueue.Clear();
                    return;
                }

                RewriteQueue.Enqueue(slot);
            }
        }

        /// <summary>当前挂起条数（含正在生效的那一条），用于回执提示。</summary>
        internal static int PendingRewriteCount
        {
            get
            {
                lock (Gate)
                {
                    return RewriteQueue.Count + (Rewrite.Armed ? 1 : 0);
                }
            }
        }

        /// <summary>当前槽用掉后推进：清空并接上队列里的下一条。</summary>
        internal static void AdvanceRewrite()
        {
            lock (Gate)
            {
                Rewrite.Clear();
                if (RewriteQueue.Count > 0)
                {
                    Rewrite.CopyFrom(RewriteQueue.Dequeue());
                    Rewrite.Armed = true;
                }
            }
        }

        // ══════════════════ 会话 ══════════════════

        internal static Session GetOrCreateSession(int playerId)
        {
            lock (Gate)
            {
                Session session;
                if (!Sessions.TryGetValue(playerId, out session))
                {
                    session = new Session { Owner = playerId };
                    Sessions[playerId] = session;
                }

                session.LastAt = global::UnityEngine.Time.realtimeSinceStartup;
                return session;
            }
        }

        internal static Session FindSession(int playerId)
        {
            lock (Gate)
            {
                Session session;
                return Sessions.TryGetValue(playerId, out session) ? session : null;
            }
        }

        internal static bool HasSession(int playerId)
        {
            lock (Gate)
            {
                return Sessions.ContainsKey(playerId);
            }
        }

        internal static void DropSession(int playerId)
        {
            lock (Gate)
            {
                Sessions.Remove(playerId);
            }
        }

        /// <summary>清掉超时的会话；返回被清掉的玩家 id（0 = 没清）。</summary>
        internal static int DropExpiredSession(int playerId, float now, int timeoutSeconds)
        {
            lock (Gate)
            {
                Session session;
                if (!Sessions.TryGetValue(playerId, out session))
                {
                    return 0;
                }

                if (timeoutSeconds > 0 && now - session.LastAt >= timeoutSeconds)
                {
                    Sessions.Remove(playerId);
                    return playerId;
                }

                return 0;
            }
        }

        // ══════════════════ 配额 ══════════════════

        /// <summary>阵营键：1 = 黑，2 = 黑幕。</summary>
        internal static int TeamKey(ServerGameRoom room, ServerGamePlayer player)
        {
            bool mastermind = room != null && (room.MasterMind == player || player.Color == EPlayerColor.Dark);
            return mastermind ? 2 : 1;
        }

        /// <summary>该玩家这一局还剩几次（<paramref name="maxPerRound"/> 为配置上限）。</summary>
        internal static int Remaining(ServerGameRoom room, ServerGamePlayer player, int maxPerRound, bool teamMode)
        {
            int used;
            lock (Gate)
            {
                if (teamMode)
                {
                    UsedByTeam.TryGetValue(TeamKey(room, player), out used);
                }
                else
                {
                    UsedByPlayer.TryGetValue(player.PublicInfo.PlayerId, out used);
                }
            }

            int remain = maxPerRound - used;
            return remain < 0 ? 0 : remain;
        }

        internal static void Consume(ServerGameRoom room, ServerGamePlayer player, bool teamMode)
        {
            lock (Gate)
            {
                int key = teamMode ? TeamKey(room, player) : player.PublicInfo.PlayerId;
                var map = teamMode ? UsedByTeam : UsedByPlayer;
                int used;
                map.TryGetValue(key, out used);
                map[key] = used + 1;
            }
        }

        /// <summary>写入失败时把刚消耗的次数还回去（配额只对「成功录入」计）。</summary>
        internal static void RefundQuota(ServerGameRoom room, ServerGamePlayer player, bool teamMode)
        {
            lock (Gate)
            {
                int key = teamMode ? TeamKey(room, player) : player.PublicInfo.PlayerId;
                var map = teamMode ? UsedByTeam : UsedByPlayer;
                int used;
                map.TryGetValue(key, out used);
                if (used > 0)
                {
                    map[key] = used - 1;
                }
            }
        }

        // ══════════════════ 伪证键 ══════════════════

        internal static string KeyOf(int playerId, int time, int roomId)
        {
            return playerId + ":" + time + ":" + roomId;
        }

        internal static void RememberFake(int playerId, int time, int roomId)
        {
            lock (Gate)
            {
                FakeKeys.Add(KeyOf(playerId, time, roomId));
            }
        }

        internal static bool IsFake(int playerId, int time, int roomId)
        {
            lock (Gate)
            {
                return FakeKeys.Contains(KeyOf(playerId, time, roomId));
            }
        }

        // ══════════════════ 生命周期 ══════════════════

        /// <summary>回大厅：本局状态全部清零（线索、配额、会话、重写槽）。</summary>
        internal static void ResetRound()
        {
            lock (Gate)
            {
                Sessions.Clear();
                FakeKeys.Clear();
                UsedByPlayer.Clear();
                UsedByTeam.Clear();
                Rewrite.Clear();
                RewriteQueue.Clear();
            }
        }

        /// <summary>热关闭：只清会话与重写槽（已录入的伪证留在设备表里，线索不撤销）。</summary>
        internal static void ClearSessions()
        {
            lock (Gate)
            {
                Sessions.Clear();
                Rewrite.Clear();
                RewriteQueue.Clear();
            }
        }
    }
}
