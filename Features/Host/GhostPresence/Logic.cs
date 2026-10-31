using System;
using System.Collections.Generic;
using Google.Protobuf;
using Protocol;
using ServerGamePacketSink = DummyClient.IPacketSink;
using ServerGameRoom = Server.Game.GameRoom;
using ServerGamePlayer = Server.Game.Player;

namespace ClueDetail.Features.Host.GhostPresence
{
    /// <summary>
    /// 鬼魂临场感的全部运行时逻辑。全部在 Unity 主线程（<c>SurvivalTick</c> 的 Postfix）。
    /// </summary>
    /// <remarks>
    /// 出站口径（与物品伪装同款）：
    /// <list type="bullet">
    /// <item>服务端真值不动 —— <c>IsAlive</c> / <c>PublicInfo.IsGhost</c> / <c>State</c> 都按原版；</item>
    /// <item>只对**观察者的副本**做 <c>S_DESPAWN</c> + <c>S_SPAWN</c>，在 <c>S_SPAWN</c> 里把
    /// <c>IsGhost</c> 改掉；</item>
    /// <item>客户端 <c>Spawn(PublicPlayerInfo)</c>（0.1.18 :32370）复用 <c>_cache</c> 里的对象，
    /// 名字不会丢；<c>Despawn</c>（:32381）只 SetActive(false)，不销毁缓存。</item>
    /// </list>
    /// <para>
    /// 节拍基准用 <c>Time.realtimeSinceStartup</c>：<c>SurviveTime</c> 是整数秒，
    /// 表达不了 0.7 秒级的安魂曲节奏。
    /// </para>
    /// </remarks>
    internal partial class GhostPresenceFeature
    {
        /// <summary>每秒维护：铃铛伪装扫描、真鬼显形调度、安魂曲。</summary>
        internal static void Tick()
        {
            ServerGameRoom room = ServerGameRoom.Instance;
            if (room == null)
            {
                return;
            }

            EGameState state = room.State;

            // ── 铃铛伪装：只在生存阶段；子开关关掉时也要把已发出去的恢复掉 ──
            if (state != EGameState.Survive || !DisguiseOn)
            {
                ClearDisguises(room);
            }
            else
            {
                ScanDisguises(room);
            }

            // ── 真鬼显形：生存与调查阶段；子开关关掉时恢复 Hide ──
            if (!RevealOn)
            {
                ClearReveals(room);
            }
            else if (state == EGameState.Survive || state == EGameState.Detective)
            {
                TickReveals(room, UnityEngine.Time.realtimeSinceStartup);
            }
        }

        /// <summary>阶段变化时调用：离开生存阶段就撤掉所有伪装。</summary>
        internal static void OnPhaseChanged(EGameState state)
        {
            ServerGameRoom room = ServerGameRoom.Instance;
            if (room == null)
            {
                return;
            }

            if (state != EGameState.Survive)
            {
                ClearDisguises(room);
            }
        }

        /// <summary>热关闭时把已经发出去的状态全部收回。</summary>
        internal static void RestoreAll()
        {
            ServerGameRoom room = ServerGameRoom.Instance;
            if (room == null)
            {
                return;
            }

            ClearDisguises(room);
            ClearReveals(room);
        }

        // ══════════════════ 铃铛伪装 ══════════════════

        private static void ScanDisguises(ServerGameRoom room)
        {
            int bell = BellId;
            List<ServerGamePlayer> alive = room.AlivePlayers;
            if (alive == null)
            {
                return;
            }

            for (int i = 0; i < alive.Count; i++)
            {
                ServerGamePlayer player = alive[i];
                if (player == null || player.PublicInfo == null)
                {
                    continue;
                }

                int id = player.PublicInfo.PlayerId;
                bool holding = player.Hand != null
                    && player.Hand.Info != null
                    && player.Hand.Info.DataId == bell;
                bool was = Disguised.Contains(id);

                if (holding && !was)
                {
                    Disguised.Add(id);
                    BroadcastRespawn(room, player, true);
                    global::ClueDetail.Core.Log.Info($"鬼魂彩蛋：{player.Name} 拿起铃铛，伪装为幽灵。");
                }
                else if (!holding && was)
                {
                    Disguised.Remove(id);
                    BroadcastRespawn(room, player, false);
                    global::ClueDetail.Core.Log.Info($"鬼魂彩蛋：{player.Name} 放下铃铛，恢复真身。");
                }
            }
        }

        private static void ClearDisguises(ServerGameRoom room)
        {
            if (Disguised.Count == 0)
            {
                return;
            }

            int[] ids = new int[Disguised.Count];
            Disguised.CopyTo(ids);
            Disguised.Clear();

            List<ServerGamePlayer> players = room.Players;
            for (int i = 0; i < ids.Length; i++)
            {
                ServerGamePlayer target = FindPlayer(players, ids[i]);
                if (target != null)
                {
                    BroadcastRespawn(room, target, false);
                }
            }
        }

        // ══════════════════ 真鬼间歇显形 ══════════════════

        private static void TickReveals(ServerGameRoom room, float now)
        {
            List<ServerGamePlayer> dead = room.DeadPlayers;
            if (dead == null)
            {
                return;
            }

            for (int i = 0; i < dead.Count; i++)
            {
                ServerGamePlayer ghost = dead[i];
                if (ghost == null || ghost.PublicInfo == null || ghost.IsAlive)
                {
                    continue;
                }

                int id = ghost.PublicInfo.PlayerId;

                if (!RevealUntil.TryGetValue(id, out float until))
                {
                    if (!NextRevealAt.TryGetValue(id, out float next))
                    {
                        NextRevealAt[id] = now + RandRange(
                            RevealIntervalMin != null ? RevealIntervalMin.Value : 20,
                            RevealIntervalMax != null ? RevealIntervalMax.Value : 35);
                        continue;
                    }

                    if (now < next)
                    {
                        continue;
                    }

                    int duration = RandRange(
                        RevealDurationMin != null ? RevealDurationMin.Value : 2,
                        RevealDurationMax != null ? RevealDurationMax.Value : 4);
                    RevealUntil[id] = now + duration;
                    NextRevealAt[id] = now + duration + RandRange(
                        RevealIntervalMin != null ? RevealIntervalMin.Value : 20,
                        RevealIntervalMax != null ? RevealIntervalMax.Value : 35);

                    // 撤 Hide：客户端 HidePlayer(false) 恢复不透明，AOI 重新纳入，
                    // 同时 StartState(Idle) 会把速度刷回普通值（无妨）。
                    ghost.State = EPlayerState.Idle;
                    BroadcastRespawn(room, ghost, true);
                    NextBellAt[id] = now;
                    RequiemBeat[id] = 0;
                    global::ClueDetail.Core.Log.Info($"鬼魂彩蛋：{ghost.Name} 显形 {duration} 秒。");
                    continue;
                }

                if (now >= until)
                {
                    RevealUntil.Remove(id);
                    NextBellAt.Remove(id);
                    RequiemBeat.Remove(id);

                    // 恢复 Hide：StartState(Hide) 会清空 SharedPlayers 并锁技能。
                    ghost.State = EPlayerState.Hide;
                    BroadcastDespawn(room, ghost);
                    global::ClueDetail.Core.Log.Info($"鬼魂彩蛋：{ghost.Name} 重新隐匿。");
                    continue;
                }

                if (RequiemOn && (!NextBellAt.TryGetValue(id, out float bellAt) || now >= bellAt))
                {
                    PlayRequiem(room, ghost);

                    int beat = RequiemBeat.TryGetValue(id, out int b) ? b : 0;
                    beat = (beat + 1) % 3;
                    RequiemBeat[id] = beat;
                    NextBellAt[id] = now + (beat == 0 ? RestGap : BeatGap);
                }
            }
        }

        private static void ClearReveals(ServerGameRoom room)
        {
            if (RevealUntil.Count == 0)
            {
                return;
            }

            var ids = new List<int>(RevealUntil.Keys);
            RevealUntil.Clear();
            NextBellAt.Clear();
            RequiemBeat.Clear();

            for (int i = 0; i < ids.Count; i++)
            {
                ServerGamePlayer ghost = FindPlayer(room.Players, ids[i]);
                if (ghost == null || ghost.IsAlive)
                {
                    continue;
                }

                ghost.State = EPlayerState.Hide;
                BroadcastDespawn(room, ghost);
            }
        }

        private static void PlayRequiem(ServerGameRoom room, ServerGamePlayer ghost)
        {
            try
            {
                // 所有人可闻：距离给到极大值，绕过原版的距离过滤。
                room.BroadcastWorldSFX(ESoundType.HandBellSfx, ghost.PublicInfo.Pos, 99999f);
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "鬼魂彩蛋：安魂曲播放失败。");
            }
        }

        // ══════════════════ 发包 ══════════════════

        /// <summary>把某个玩家的「当前状态」按收件人重发一次（先 despawn 再 spawn）。</summary>
        private static void BroadcastRespawn(ServerGameRoom room, ServerGamePlayer target, bool asGhost)
        {
            List<ServerGamePlayer> players = room.Players;
            if (players == null || target.PublicInfo == null)
            {
                return;
            }

            for (int i = 0; i < players.Count; i++)
            {
                ServerGamePlayer observer = players[i];
                if (observer == null || observer == target || observer.Session == null)
                {
                    continue;
                }

                try
                {
                    SendSpawn(observer, target, asGhost);
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "鬼魂彩蛋：重发玩家状态失败。");
                }
            }
        }

        private static void BroadcastDespawn(ServerGameRoom room, ServerGamePlayer target)
        {
            List<ServerGamePlayer> players = room.Players;
            if (players == null || target.PublicInfo == null)
            {
                return;
            }

            for (int i = 0; i < players.Count; i++)
            {
                ServerGamePlayer observer = players[i];
                if (observer == null || observer == target || observer.Session == null)
                {
                    continue;
                }

                try
                {
                    observer.Session.Send(new S_DESPAWN { PlayerId = target.PublicInfo.PlayerId });
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "鬼魂彩蛋：隐藏玩家失败。");
                }
            }
        }

        /// <summary>给单个观察者重发「目标玩家」，并按需改写 IsGhost。</summary>
        internal static void SendSpawn(ServerGamePlayer observer, ServerGamePlayer target, bool asGhost)
        {
            if (observer == null || observer.Session == null || target == null || target.PublicInfo == null)
            {
                return;
            }

            var info = target.PublicInfo.Clone();
            info.IsGhost = asGhost;

            observer.Session.Send(new S_DESPAWN { PlayerId = target.PublicInfo.PlayerId });
            observer.Session.Send(new S_SPAWN { Player = info });
        }

        /// <summary>AOI 重新介绍某人时补发一次：保证伪装者不会因为缓存重建而变回真人。</summary>
        internal static void OnAddPlayerSent(IMessage packet, ServerGamePacketSink sink, int recipient)
        {
            if (!(packet is S_ADD_PLAYER add) || Disguised.Count == 0)
            {
                return;
            }

            if (recipient == add.PlayerId)
            {
                return;
            }

            ServerGameRoom room = ServerGameRoom.Instance;
            if (room == null)
            {
                return;
            }

            ServerGamePlayer target = FindPlayer(room.Players, add.PlayerId);
            if (target == null)
            {
                return;
            }

            var info = target.PublicInfo.Clone();
            info.IsGhost = true;
            // 立刻补一帧 S_SPAWN：客户端 Spawn(S_ADD_PLAYER) 建出来的是「真人缓存」，
            // 不补这一下就会以真身显示。
            sink?.Send(new S_SPAWN { Player = info });
        }

        private static ServerGamePlayer FindPlayer(List<ServerGamePlayer> players, int id)
        {
            if (players == null)
            {
                return null;
            }

            for (int i = 0; i < players.Count; i++)
            {
                ServerGamePlayer p = players[i];
                if (p != null && p.PublicInfo != null && p.PublicInfo.PlayerId == id)
                {
                    return p;
                }
            }

            return null;
        }

        private static int RandRange(int min, int max)
        {
            if (max <= min)
            {
                return min;
            }

            return UnityEngine.Random.Range(min, max + 1);
        }
    }
}
