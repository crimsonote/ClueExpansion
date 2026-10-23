using System.Collections.Generic;
using ClueDetail.Core;
using HarmonyLib;
using Protocol;
using ServerDeviceManager = Server.Game.DeviceManager;
using ServerGamePlayer = Server.Game.Player;
using ServerGameRoom = Server.Game.GameRoom;
using ServerTimeManager = Server.Game.TimeManager;

namespace ClueDetail.Features.Host.GameChat
{
    internal partial class GameChatFeature
    {
        /// <summary>报告窗口长度（游戏内分钟：1 分钟 = 1 秒 SurviveTime）。</summary>
        /// <remarks>
        /// 用户口径 2026-10-19/20：**游戏内时钟 20 分钟 = 20 秒**
        /// （<c>FormatTimeSentence</c> 里 <c>小时 = time/60</c>，所以 1 游戏小时 = 60 秒）。
        /// </remarks>
        private const int ReportWindowSeconds = 20;

        /// <summary>会议报告的延迟（毫秒）：客户端切场景后第一帧还会做一次性初始化。</summary>
        private const int TrialReportDelayMs = 500;

        /// <summary>
        /// 进入会议时，给**每个玩家各发一份他自己的线索报告 + 一份帮助**。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>顺序（用户口径 2026-10-20）</b>：<b>先线索、后帮助</b>；
        /// 帮助内容必须与 <c>/help</c> 的完全一致（同一个 <see cref="AvailableCommands"/> 投影）。
        /// </para>
        /// <para>
        /// <b>窗口</b>：<c>[案件时间前 20 分钟, 报告时]</c> —— 不是"到死亡那一刻为止"。
        /// 案件时间在**未报告**时必须是客户端看到的**模糊区间下界**
        /// （<c>Server.Game.Corpse.GetDisplayTime</c>，0.1.18 :171982），
        /// 否则就会把真实死亡时间通过标题泄露给没去报告的人。
        /// </para>
        /// <para>
        /// ⚠️ 没有尸体（没发生死亡却强行进会议）必须安全退出：只记一行 Info，不抛异常、不发包。
        /// </para>
        /// </remarks>
        internal static void ReportOnTrialStart(ServerGameRoom room)
        {
            if (room == null || room.Players == null || !FeatureLoader.Enabled<GameChatFeature>())
            {
                return;
            }

            try
            {
                int from;
                int to;
                if (!TryResolveWindow(room, out from, out to))
                {
                    global::ClueDetail.Core.Log.Info(
                        "游戏内命令：本局没有可供推算的案件时间（无尸体记录），跳过会议自动线索报告。");
                    return;
                }

                int sent = 0;
                for (int i = 0; i < room.Players.Count; i++)
                {
                    ServerGamePlayer p = room.Players[i];
                    if (p == null || p.Session == null || p.PublicInfo == null)
                    {
                        continue;
                    }

                    try
                    {
                        SendReport(room, p, from, to);
                        sent++;
                    }
                    catch (global::System.Exception ex)
                    {
                        global::ClueDetail.Core.Log.Warn(
                            $"游戏内命令：给 #{p.PublicInfo.PlayerId} 推线索报告失败（{ex.Message}）。");
                    }
                }

                global::ClueDetail.Core.Log.Info(
                    $"游戏内命令：会议开始，已向 {sent} 名玩家推送个人线索报告" +
                    $"（窗口 {ClockOf(from)} ~ {ClockOf(to)}）。");
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "游戏内命令：会议自动报告失败（已安全跳过，不影响会议）。");
            }
        }

        /// <summary>给一个玩家推送「线索报告」→「帮助」两条消息。</summary>
        private static void SendReport(ServerGameRoom room, ServerGamePlayer player, int from, int to)
        {
            bool seesAll = !player.IsAlive || player.IsSpectator;
            string query = seesAll ? string.Empty : $"t:{from}-{to}";
            List<ClueRow> rows = Search(player, query);

            var lines = new List<string>();
            lines.Add(seesAll
                ? $"【你的线索】共 {rows.Count} 条（你已死亡/旁观：全部线索）"
                : $"【你的线索】{ClockOf(from)} ~ {ClockOf(to)} 共 {rows.Count} 条");

            for (int i = 0; i < rows.Count; i++)
            {
                lines.Add(rows[i].Text);
            }

            if (rows.Count == 0)
            {
                lines.Add("（这个时段没有你搜集到的线索）");
            }

            // ⚠️ 先线索（用户口径 2026-10-20）。
            Reply(room, player, null, lines);

            // 再帮助，且与 /help 完全一致（同一个可用命令表）。
            Reply(room, player, null, HelpLines(room));
        }

        /// <summary><c>/help</c> 与会议报告共用的帮助正文。</summary>
        private static List<string> HelpLines(ServerGameRoom room)
        {
            var lines = new List<string> { "【ClueDetail 命令】" };
            lines.AddRange(AvailableCommands(room));
            return lines;
        }

        /// <summary>
        /// 解析报告窗口 <c>[from, to]</c>（<c>SurviveTime</c> 秒）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>未报告时不能泄露真实死亡时间</b>：原版客户端看到的是模糊区间
        /// （<c>Server.Game.Corpse.GetDisplayTime</c>，0.1.18 :171982-172010）。
        /// 真实死亡 10:12 的玩家看到的是 "10:08 ~ 10:28" 这种范围 ——
        /// 起点必须从**那个区间的下界**再往前推 20 分钟，直接用 <c>DeadTime</c> 等于把精确时刻
        /// 写进"线索窗口"的标题里。
        /// </para>
        /// <para>
        /// 选哪具：优先"已报告"的（此时 <c>timeEnd == 0</c>、原版给精确时间）；
        /// 否则取最早死亡的那具。
        /// </para>
        /// <para>
        /// <c>to</c> = **报告时**（<c>TimeManager.SurviveTime</c>），并保证不小于案件时间。
        /// </para>
        /// </remarks>
        private static bool TryResolveWindow(ServerGameRoom room, out int from, out int to)
        {
            from = 0;
            to = 0;

            var dm = ServerDeviceManager.Instance;
            var corpses = dm == null ? null : dm.Corpses;
            if (corpses == null || corpses.Count == 0)
            {
                return false;
            }

            int reportedId = 0;
            int reportedDead = 0;
            int earliestId = 0;
            int earliestDead = 0;

            if (room.CorpseMetas != null)
            {
                foreach (var pair in room.CorpseMetas)
                {
                    var meta = pair.Value;
                    if (meta == null || meta.DeadTime <= 0)
                    {
                        continue;
                    }

                    if (earliestId == 0 || meta.DeadTime < earliestDead)
                    {
                        earliestId = pair.Key;
                        earliestDead = meta.DeadTime;
                    }

                    if (meta.IsReport && (reportedId == 0 || meta.DeadTime < reportedDead))
                    {
                        reportedId = pair.Key;
                        reportedDead = meta.DeadTime;
                    }
                }
            }

            int targetId = reportedId != 0 ? reportedId : earliestId;
            if (targetId == 0)
            {
                return false;
            }

            int caseTime = reportedId != 0 ? reportedDead : earliestDead;
            int caseEnd = 0;

            global::Server.Game.Corpse corpse = FindCorpse(corpses, targetId);
            if (corpse != null)
            {
                // 原版口径：已报告 ⇒ (DeadTime, 0)；未报告 ⇒ (模糊下界, 模糊上界)。
                corpse.GetDisplayTime(out caseTime, out caseEnd);
            }

            int now = (int)ServerTimeManager.Instance.SurviveTime;
            to = now > caseTime ? now : caseTime;

            from = caseTime - ReportWindowSeconds;
            if (from < 0)
            {
                from = 0;
            }

            return true;
        }

        /// <summary>按 <c>DeviceId</c> 在尸体列表里找一具。</summary>
        private static global::Server.Game.Corpse FindCorpse(
            global::System.Collections.Generic.IReadOnlyList<global::Server.Game.Corpse> corpses, int corpseId)
        {
            for (int i = 0; i < corpses.Count; i++)
            {
                var c = corpses[i];
                if (c != null && c.DeviceInfo != null && c.DeviceInfo.DeviceId == corpseId)
                {
                    return c;
                }
            }

            return null;
        }

        /// <summary>阶段切换钩子：回大厅时清空线索索引。</summary>
        /// <remarks>
        /// ⚠️ 只做 Postfix（不吞原方法）：阶段切换本身绝不能因为本功能失败。
        /// ⚠️ **会议报告不在这里发** —— 见 <see cref="TrialStartHook"/>。
        /// 这个 public 重载只是**发起**切换（<c>State</c> 立刻改、<c>IsTransitioning = true</c>），
        /// 真正的 <c>StartTrial()</c> 要等 <c>BroadcastPacketAndWaitResponse</c> 的回调（0.1.18 :173063）。
        /// 在这里发消息时客户端还在过渡、聊天框可能没建好 ⇒ 消息被丢掉。
        /// </remarks>
        [HarmonyPatch(typeof(ServerGameRoom), nameof(ServerGameRoom.ChangeGameState), new[] { typeof(EGameState) })]
        internal static class PhaseHook
        {
            [HarmonyPostfix]
            private static void Postfix(EGameState state)
            {
                if (!FeatureLoader.Enabled<GameChatFeature>()) return;

                try
                {
                    if (state == EGameState.Lobby)
                    {
                        Clear();          // 线索不跨局
                    }
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "游戏内命令：阶段钩子失败（已安全忽略）。");
                }
            }
        }

        /// <summary>
        /// 会议**真正开始**时（<c>StartTrial</c>）推个人线索报告。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 挂 <c>GameRoom.StartTrial</c>（private，字符串名）：它是 <c>StartState(Trial)</c> 里的那一步，
        /// 此时阶段切换的客户端应答已经收齐、聊天 UI 已经建好。
        /// </para>
        /// <para>
        /// 再延迟 <see cref="TrialReportDelayMs"/> 毫秒：客户端切场景后第一帧仍有一次性初始化，
        /// 立刻发偶尔会被吃掉。
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(ServerGameRoom), "StartTrial")]
        internal static class TrialStartHook
        {
            [HarmonyPostfix]
            private static void Postfix(ServerGameRoom __instance)
            {
                if (!FeatureLoader.Enabled<GameChatFeature>()) return;

                try
                {
                    ServerGameRoom room = __instance ?? ServerGameRoom.Instance;
                    if (room == null)
                    {
                        return;
                    }

                    room.PushAfter(TrialReportDelayMs, delegate
                    {
                        try
                        {
                            ReportOnTrialStart(room);
                        }
                        catch (global::System.Exception ex)
                        {
                            global::ClueDetail.Core.Log.Exception(
                                ex, "游戏内命令：会议自动报告失败（已安全跳过，不影响会议）。");
                        }
                    });
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "游戏内命令：会议报告调度失败（已安全忽略）。");
                }
            }
        }
    }
}
