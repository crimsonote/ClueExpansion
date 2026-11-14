using System.Collections.Generic;
using ClueDetail.Core;
using ClueDetail.Features.Host.GameChat;
using Protocol;
using ServerChatDevice = Server.Game.ChatDevice;
using ServerGamePlayer = Server.Game.Player;
using ServerGameRoom = Server.Game.GameRoom;
using GameChatFeature = ClueDetail.Features.Host.GameChat.GameChatFeature;

namespace ClueDetail.Features.Host.ClueFake
{
    /// <summary>
    /// <c>/f</c> 命令：完整参数一次给完，或进入交互式问答补全。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>只能在发信机的聊天频道里用</b>：原版 <c>RelayDeviceChat</c>（0.1.18 :177827）
    /// 要求 <c>State == Survive</c>、存活、且 <c>ChatDevice.IsOccupiedBy(sender)</c>；
    /// 本命令复刻同一组判据。普通聊天（<c>NormalChat</c>）里输入 <c>/f</c> 只会收到一句提示。
    /// </para>
    /// <para>
    /// <b>交互式</b>：参数不完整时登记一份会话（<see cref="ClueFakeState.Session"/>），
    /// 玩家在发信机里的下一条输入就是答案；<c>exit</c> 取消，超时 / 换阶段 / 玩家离开也会清掉。
    /// </para>
    /// <para>
    /// <b>参数</b>：<c>[时间] [人物] [房间] [活动]</c>：
    /// 时间 = <c>07:00am/pm</c> 或游戏内分钟；人物 = 昵称 / <c>#id</c> / <c>b</c> 黑 / <c>m</c> 黑幕；
    /// 房间 = 编号或名字；活动 = 该房间候选的编号。<c>r</c> = 在当前候选里随机；
    /// <c>x</c>（房间或活动位）= 不录伪证，改为重写下一条真实线索的时间与人物。
    /// </para>
    /// </remarks>
    internal static class ClueFakeCommand
    {
        /// <summary>命令名（含别名）——只认这三个。</summary>
        internal static bool IsName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            return name.Equals("f", global::System.StringComparison.OrdinalIgnoreCase)
                || name.Equals("fake", global::System.StringComparison.OrdinalIgnoreCase)
                || name.Equals("lie", global::System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>该玩家有没有进行中的录入会话（供聊天包 Prefix 的线程读，纯内存）。</summary>
        internal static bool HasSession(int playerId)
        {
            return ClueFakeState.HasSession(playerId);
        }

        // ══════════════════ 入口 ══════════════════

        /// <summary>
        /// <c>/f [时间] [人物] [房间] [活动]</c> 的主入口（已由 <c>room.Push</c> 排到主线程）。
        /// </summary>
        /// <param name="channel">
        /// 消息来源频道。本命令暂时只接受**黑方秘密终端**（<c>SecretChat</c>）；
        /// 普通聊天/公开发信机里输入只回一句提示（也要吞掉，避免被当成留言广播）。
        /// </param>
        internal static void Handle(
            ServerGameRoom room, ServerGamePlayer player, int deviceId, string arg, ChatChannelMask channel)
        {
            if (!FeatureLoader.Enabled<ClueFakeFeature>()
                || (!ClueFakeFeature.BlackEnabled.Value && player.Color == EPlayerColor.Black))
            {
                return;
            }

            if (channel != ChatChannelMask.Secret)
            {
                Reply(room, player, deviceId, channel,
                    "线索伪造只能在黑方秘密终端中使用。", null);
                return;
            }

            ServerChatDevice chat;
            string error;
            if (!Validate(room, player, deviceId, out chat, out error))
            {
                Reply(room, player, deviceId, ChatChannelMask.Secret, error);
                return;
            }

            ClueFakeState.Session session = ClueFakeState.GetOrCreateSession(player.PublicInfo.PlayerId);
            // 每次 /f 都从头开始：清掉上一轮残留的字段，避免「明明没填却跳过某一步」。
            session.Clear();

            string[] parts = (arg ?? string.Empty)
                .Split(new[] { ' ', '\t' }, global::System.StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
            {
                Prompt(room, player, deviceId, session);
                return;
            }

            // /f h | /f help：只打印本命令自身的帮助（不进入录入/改写流程）。
            if (parts.Length == 1
                && (parts[0].Equals("h", global::System.StringComparison.OrdinalIgnoreCase)
                    || parts[0].Equals("help", global::System.StringComparison.OrdinalIgnoreCase)))
            {
                Help(room, player, deviceId);
                return;
            }

            // 按位置解析；**任何一项非法都视为 "-"**（留到交互式补全），不再中断整条命令。
            // "-" 是占位符：这一项暂时不提供（位置仍然按 i 对齐）。
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 3)
                {
                    Reply(room, player, deviceId, ChatChannelMask.Secret,
                        "参数过多（最多 4 个：时间 人物 房间 活动）。");
                    Prompt(room, player, deviceId, session);
                    return;
                }

                if (parts[i] == "-")
                {
                    MarkDash(session, i);
                    continue;
                }

                if (!Feed(room, player, chat, session, i, parts[i], out error))
                {
                    // 非法值 ⇒ 当作这一项没提供（交互式再问），而不是整条命令失败。
                    MarkDash(session, i);
                    continue;
                }

                if (session.Complete)
                {
                    break;
                }
            }

            // 改写模式（任一 x）下「缺省 = 真实」：把没给值、也没显式 - 的字段补成真实。
            session.FinalizeRewriteDefaults();

            if (session.Complete)
            {
                Execute(room, player, chat, session);
                return;
            }

            Prompt(room, player, deviceId, session);
        }

        /// <summary>把第 <paramref name="index"/> 个位置标记为「显式未提供」，交互式会问它。</summary>
        private static void MarkDash(ClueFakeState.Session session, int index)
        {
            switch (index)
            {
                case 0: session.DashTime = true; break;
                case 1: session.DashPerson = true; break;
                case 2: session.DashRoom = true; break;
                case 3: session.DashActivity = true; break;
            }
        }

        /// <summary>交互式会话的闲置上限（秒）：超时就丢弃，避免玩家忘掉 exit 后聊天被一直吞掉。</summary>
        internal const int SessionTimeoutSeconds = 120;

        /// <summary>
        /// 交互式：把玩家在发信机里的下一条输入当作当前步骤的答案。
        /// </summary>
        internal static void HandleAnswer(ServerGameRoom room, ServerGamePlayer player, int deviceId, string text)
        {
            if (!FeatureLoader.Enabled<ClueFakeFeature>()
                || (!ClueFakeFeature.BlackEnabled.Value && player.Color == EPlayerColor.Black))
            {
                return;
            }

            int expired = ClueFakeState.DropExpiredSession(
                player.PublicInfo.PlayerId,
                global::UnityEngine.Time.realtimeSinceStartup,
                SessionTimeoutSeconds);
            if (expired != 0)
            {
                Reply(room, player, deviceId, ChatChannelMask.Secret, "录入会话已超时，已取消。");
                return;
            }

            ClueFakeState.Session session = ClueFakeState.FindSession(player.PublicInfo.PlayerId);
            if (session == null)
            {
                return;
            }

            ServerChatDevice chat;
            string error;
            if (!Validate(room, player, deviceId, out chat, out error))
            {
                ClueFakeState.DropSession(player.PublicInfo.PlayerId);
                Reply(room, player, deviceId, ChatChannelMask.Secret, error);
                return;
            }

            string trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Equals("exit", global::System.StringComparison.OrdinalIgnoreCase))
            {
                ClueFakeState.DropSession(player.PublicInfo.PlayerId);
                Reply(room, player, deviceId, ChatChannelMask.Secret, "已取消录入。");
                return;
            }

            if (!Feed(room, player, chat, session, FirstMissingIndex(session), trimmed, out error))
            {
                Reply(room, player, deviceId, ChatChannelMask.Secret, error);
                Prompt(room, player, deviceId, session);
                return;
            }

            // 改写模式下同样「缺省 = 真实」：答完一项后把其余没给的字段补成真实。
            session.FinalizeRewriteDefaults();

            if (session.Complete)
            {
                Execute(room, player, chat, session);
                return;
            }

            Prompt(room, player, deviceId, session);
        }

        /// <summary>阶段切换 / 离开时清会话（主线程）。</summary>
        internal static void ClearAll()
        {
            ClueFakeState.ClearSessions();
        }

        // ══════════════════ 校验 ══════════════════

        private static bool Validate(
            ServerGameRoom room, ServerGamePlayer player, int deviceId, out ServerChatDevice chat, out string error)
        {
            chat = null;
            error = null;

            if (room == null || player == null || player.PublicInfo == null)
            {
                error = "房间或玩家不可用。";
                return false;
            }

            // 与原版 RelayDeviceChat（0.1.18 :177827）同一组前置条件。
            if (room.State != EGameState.Survive)
            {
                error = "线索伪造只能在生存阶段使用。";
                return false;
            }

            if (!player.IsAlive)
            {
                error = "死者不能执行线索伪造。";
                return false;
            }

            if (!IsBlackTeam(room, player))
            {
                error = "只有黑方或黑幕能执行线索伪造。";
                return false;
            }

            chat = ClueFakeLogic.FindChatDevice(deviceId);
            if (chat == null || !chat.IsOccupiedBy(player))
            {
                error = "必须在发信机上执行（先打开一台发信机）。";
                return false;
            }

            return true;
        }

        /// <summary>黑队：黑（<c>Black</c>）或黑幕（<c>Dark</c> / <c>MasterMind</c>）。</summary>
        private static bool IsBlackTeam(ServerGameRoom room, ServerGamePlayer player)
        {
            return room.MasterMind == player
                || player.Color == EPlayerColor.Dark   // 黑幕
                || player.Color == EPlayerColor.Black; // 黑（杀手）
        }

        // ══════════════════ 解析管线 ══════════════════

        /// <summary>
        /// 把第 <paramref name="index"/> 个参数喂给会话（<c>0</c> 时间、<c>1</c> 人物、
        /// <c>2</c> 房间、<c>3</c> 活动）。
        /// </summary>
        /// <remarks>
        /// <c>x</c> 只允许出现在房间 / 活动位：一旦出现就进入重写模式，房间与活动被忽略。
        /// <c>r</c> 只在房间 / 活动位生效。
        /// </remarks>
        private static bool Feed(
            ServerGameRoom room,
            ServerGamePlayer player,
            ServerChatDevice chat,
            ClueFakeState.Session session,
            int index,
            string token,
            out string error)
        {
            error = null;
            string t = (token ?? string.Empty).Trim();
            if (t.Length == 0)
            {
                error = "参数不能为空。";
                return false;
            }

            switch (index)
            {
                case 0:
                {
                    if (IsRewrite(t))
                    {
                        session.RealTime = true;
                        session.HasTime = true;
                        session.Step = ClueFakeState.Step.Person;
                        return true;
                    }

                    // 偏移值：+25 / -15（分钟）。新写模式 = 命令执行时刻 + 偏移；
                    // x 模式 = 下一条真实线索的时间 + 偏移。
                    if (t.Length > 1 && (t[0] == '+' || t[0] == '-'))
                    {
                        int off;
                        if (global::System.Int32.TryParse(t.Substring(1), out off))
                        {
                            session.HasTimeOffset = true;
                            session.TimeOffset = t[0] == '-' ? -off : off;
                            session.HasTime = true;
                            session.Step = ClueFakeState.Step.Person;
                            return true;
                        }
                    }

                    int minutes;
                    if (!ClueFakeLogic.TryParseClock(t, out minutes, out error))
                    {
                        return false;
                    }

                    if (!ClueFakeLogic.IsWritableClock(minutes, out error))
                    {
                        return false;
                    }

                    session.Time = minutes;
                    session.HasTime = true;
                    session.Step = ClueFakeState.Step.Person;
                    return true;
                }

                case 1:
                {
                    if (IsRewrite(t))
                    {
                        session.RealPerson = true;
                        session.HasPerson = true;
                        session.Step = ClueFakeState.Step.Room;
                        return true;
                    }

                    int pid;
                    if (!ClueFakeLogic.TryResolvePerson(room, t, out pid, out error))
                    {
                        return false;
                    }

                    session.PersonId = pid;
                    session.HasPerson = true;
                    session.Step = ClueFakeState.Step.Room;
                    return true;
                }

                case 2:
                {
                    // x = 保留真实房间；改写模式下**不允许单独伪造房间**（会跟设备号错配），一律按真实处理。
                    if (IsRewrite(t) || session.RewriteMode)
                    {
                        session.RealRoom = true;
                        session.HasRoom = true;
                        session.Step = ClueFakeState.Step.Activity;
                        return true;
                    }

                    int roomId;
                    if (IsRandom(t))
                    {
                        List<int> pool = ClueFakeLogic.RoomPool();
                        if (pool.Count == 0)
                        {
                            error = "没有可伪造的房间。";
                            return false;
                        }

                        roomId = pool[new global::System.Random().Next(pool.Count)];
                    }
                    else if (!ClueFakeLogic.TryResolveRoom(t, out roomId, out error))
                    {
                        return false;
                    }

                    if (!HasDevice(roomId))
                    {
                        error = "房间「" + ClueFakeLogic.RoomNameOf(roomId) + "」没有可伪造的设备活动。";
                        return false;
                    }

                    session.RoomId = roomId;
                    session.HasRoom = true;
                    session.Step = ClueFakeState.Step.Activity;
                    return true;
                }

                case 3:
                {
                    // x = 保留真实活动；改写模式下活动同样不能单独伪造，一律按真实处理。
                    if (IsRewrite(t) || session.RewriteMode)
                    {
                        session.RealActivity = true;
                        session.HasActivity = true;
                        session.Step = ClueFakeState.Step.Activity;
                        return true;
                    }

                    // 房间还没定（例如 `/f 9:24pm b - 1`）：活动编号没有房间做前提，
                    // 直接忽略，等交互式先把房间问出来。
                    if (!session.HasRoom)
                    {
                        return true;
                    }

                    List<EWhatType> options = ClueFakeLogic.ActivitiesOfRoom(session.RoomId);
                    if (options.Count == 0)
                    {
                        error = "该房间没有可伪造的活动。";
                        return false;
                    }

                    EWhatType what;
                    if (IsRandom(t))
                    {
                        what = options[new global::System.Random().Next(options.Count)];
                    }
                    else if (!ClueFakeLogic.TryResolveActivity(session.RoomId, t, out what, out error))
                    {
                        return false;
                    }

                    session.What = (int)what;
                    session.HasActivity = true;
                    session.Step = ClueFakeState.Step.Activity;
                    return true;
                }

                default:
                    error = "参数过多（最多 4 个：时间 人物 房间 活动）。";
                    return false;
            }
        }

        private static bool IsRewrite(string token)
        {
            return token.Equals("x", global::System.StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRandom(string token)
        {
            return token.Equals("r", global::System.StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasDevice(int roomId)
        {
            return ClueFakeLogic.ActivitiesOfRoom(roomId).Count > 0;
        }

        // ══════════════════ 执行 ══════════════════

        private static void Execute(
            ServerGameRoom room, ServerGamePlayer player, ServerChatDevice chat, ClueFakeState.Session session)
        {
            // 回执走发信机频道（玩家是在发信机 UI 里输入的），所以这里取当前发信机的设备号。
            int deviceId = chat == null ? 0 : chat.ID;
            string error;

            if (!ClueFakeLogic.TryConsumeQuota(room, player, out error))
            {
                ClueFakeState.DropSession(player.PublicInfo.PlayerId);
                Reply(room, player, deviceId, ChatChannelMask.Secret, error);
                return;
            }

            if (session.RewriteMode)
            {
                int timeout = ClueFakeFeature.RewriteTimeoutSeconds == null
                    ? 300
                    : ClueFakeFeature.RewriteTimeoutSeconds.Value;

                var slot = new ClueFakeState.RewriteSlot();
                slot.Armed = true;
                slot.OwnerId = player.PublicInfo.PlayerId;
                slot.MarkOnly = session.RealTime && session.RealPerson && !session.HasTimeOffset;
                slot.RealTime = session.RealTime && !session.HasTimeOffset;
                slot.HasTimeOffset = session.HasTimeOffset;
                slot.TimeOffset = session.TimeOffset;
                slot.RealPerson = session.RealPerson;
                slot.RealRoom = session.RealRoom;
                slot.RealActivity = session.RealActivity;
                slot.Time = session.Time;
                slot.PersonId = session.PersonId;
                slot.RoomId = session.RoomId;
                slot.ExpireAt = timeout <= 0
                    ? 0f
                    : global::UnityEngine.Time.realtimeSinceStartup + timeout;

                ClueFakeState.ArmRewrite(slot, ClueFakeFeature.QueueXMode);

                // 代价与普通录入一致：发信机留下「黑/黑幕 留下了消息」的角色线索。
                // RecordTrace 自带 30 秒桶合并（同角色 + 同设备 + 同桶只刷新时间），不会刷屏。
                ClueFakeLogic.RecordTrace(chat, player, room);

                ClueFakeState.DropSession(player.PublicInfo.PlayerId);

                int pending = ClueFakeState.PendingRewriteCount;

                string rewriteMessage = slot.MarkOnly
                    ? "已记录：下一条**你**的线索将原样保留，但会被标记为伪造。"
                    : "已记录改写：下一条**你**的线索"
                        + (session.HasTimeOffset
                            ? "时间偏移 " + (session.TimeOffset >= 0 ? "+" : "") + session.TimeOffset + " 分钟"
                            : (session.RealTime ? "时间保留真实" : "时间改为 " + ShortClockOf(session.Time)))
                        + "，"
                        + (session.RealPerson ? "身份保留真实" : "身份改为 " + ClueFakeLogic.PersonNameOf(room, session.PersonId))
                        + "。剩余可录入次数 " + ClueFakeLogic.RemainingText(room, player) + "。"
                        + (pending > 1 ? "（前面还有 " + (pending - 1) + " 条排队）" : "");

                Reply(room, player, deviceId, ChatChannelMask.Secret, rewriteMessage);
                return;
            }

            int writeTime = session.Time;
            if (session.HasTimeOffset)
            {
                writeTime = ClueFakeLogic.NowMinutes() + session.TimeOffset;
                if (!ClueFakeLogic.IsWritableClock(writeTime, out error))
                {
                    ClueFakeState.RefundQuota(room, player, ClueFakeFeature.TeamQuota);
                    ClueFakeState.DropSession(player.PublicInfo.PlayerId);
                    Reply(room, player, deviceId, ChatChannelMask.Secret, error);
                    return;
                }
            }

            if (!ClueFakeLogic.TryWriteFake(
                    room, session.PersonId, writeTime, session.RoomId, (EWhatType)session.What, out error))
            {
                // 写入失败不扣次数（把刚消耗的还回去）：配额是"成功录入"才计。
                ClueFakeState.RefundQuota(room, player, ClueFakeFeature.TeamQuota);
                ClueFakeState.DropSession(player.PublicInfo.PlayerId);
                Reply(room, player, deviceId, ChatChannelMask.Secret, error);
                return;
            }

            // 代价：发信机留下「黑/黑幕 留下了消息」的角色线索（匿名，不写真实玩家 id）。
            ClueFakeLogic.RecordTrace(chat, player, room);

            ClueFakeState.DropSession(player.PublicInfo.PlayerId);

            string message = "虚假线索["
                + ShortClockOf(writeTime) + "，"
                + ClueFakeLogic.PersonNameOf(room, session.PersonId) + "于"
                + ClueFakeLogic.RoomNameOf(session.RoomId)
                + ClueFakeLogic.WhatNameOf((EWhatType)session.What)
                + "]已录入。剩余可录入次数 " + ClueFakeLogic.RemainingText(room, player) + "。";

            Reply(room, player, deviceId, ChatChannelMask.Secret, message);
        }

        // ══════════════════ 提示 ══════════════════

        /// <summary>
        /// 第一个还没填的步骤。
        /// </summary>
        /// <remarks>
        /// ⚠️ 不能直接用 <c>session.Step</c>：<c>/f 09:24pm - 广播室</c> 这类带 <c>-</c> 占位符的命令
        /// 会先填后面的项（房间），Step 却停在人物 —— 必须按「哪个 Has 还是 false」来判断。
        /// </remarks>
        private static ClueFakeState.Step FirstMissing(ClueFakeState.Session session)
        {
            if (session.RewriteMode)
            {
                if (!session.RealTime && !session.HasTime)
                {
                    return ClueFakeState.Step.Time;
                }

                if (!session.RealPerson && !session.HasPerson)
                {
                    return ClueFakeState.Step.Person;
                }

                if (!session.RealRoom && !session.HasRoom)
                {
                    return ClueFakeState.Step.Room;
                }

                return ClueFakeState.Step.Activity;
            }

            if (!session.HasTime)
            {
                return ClueFakeState.Step.Time;
            }

            if (!session.HasPerson)
            {
                return ClueFakeState.Step.Person;
            }

            if (!session.HasRoom)
            {
                return ClueFakeState.Step.Room;
            }

            return ClueFakeState.Step.Activity;
        }

        private static int FirstMissingIndex(ClueFakeState.Session session)
        {
            return (int)FirstMissing(session) - 1;
        }

        /// <summary><c>/f h</c> / <c>/f help</c>：本命令自身的帮助。</summary>
        private static void Help(ServerGameRoom room, ServerGamePlayer player, int deviceId)
        {
            var lines = new List<string>
            {
                "【线索伪造 /f 帮助】",
                "语法：/f [时间] [人物] [房间] [活动]",
                "时间：07:00am/pm、21:49（24 小时制）、469（分钟）",
                "      +25 / -15 = 基于执行时刻偏移（分钟；x 模式基于下一条线索）",
                "人物：昵称 / #id / 数字 id / b=黑 / m=黑幕",
                "房间：选项编号（#n）或房间名；活动：该房间的选项编号",
                "r = 候选里随机；- = 这一项留到交互式补全",
                "x = 保留真实值（进入改写模式，等**你**的下一条真实线索）",
                "  /f x x x    完全原样保留，只把下一条线索标记为伪造",
                "  /f x m x  时间/房间真实，只把身份改成黑幕玩家",
                "exit 取消；交互式闲置 120 秒自动取消。",
            };
            Reply(room, player, deviceId, ChatChannelMask.Secret, null, lines);
        }
        private static void Prompt(ServerGameRoom room, ServerGamePlayer player, int deviceId, ClueFakeState.Session session)
        {
            var lines = new List<string>();

            switch (FirstMissing(session))
            {
                case ClueFakeState.Step.Time:
                    lines.Add("请输入时间，格式为07:00am/pm，输入exit退出");
                    lines.Add("#x 保留真实时间（进入改写模式）");
                    break;

                case ClueFakeState.Step.Person:
                    lines.Add("请选择记录身份，格式为一个数字，也可兼容id输入，可用选项");
                    lines.AddRange(PersonOptions(room));
                    lines.Add("#x 保留真实身份（进入改写模式）");
                    break;

                case ClueFakeState.Step.Room:
                    lines.Add("请选择房间，可用选项");
                    lines.AddRange(RoomOptions());
                    lines.Add("#r 在可用列表内随机选择");
                    lines.Add("#x 保留真实房间（改写模式下房间不能单独伪造）");
                    break;

                case ClueFakeState.Step.Activity:
                    lines.Add("请选择设施与活动，可用选项");
                    lines.AddRange(ActivityOptions(session.RoomId));
                    lines.Add("#r 在可用列表中随机选择");
                    lines.Add("#x 保留真实活动（改写模式下活动不能单独伪造）");
                    break;
            }

            Reply(room, player, deviceId, ChatChannelMask.Secret, null, lines);
        }

        private static IEnumerable<string> PersonOptions(ServerGameRoom room)
        {
            var list = new List<string>();
            for (int i = 0; room.Players != null && i < room.Players.Count; i++)
            {
                ServerGamePlayer p = room.Players[i];
                if (p == null || p.PublicInfo == null || string.IsNullOrEmpty(p.Name))
                {
                    continue;
                }

                list.Add("#" + p.PublicInfo.PlayerId + " " + p.Name);
            }

            // 每行最多 2 个，避免超过气泡宽度。
            var lines = new List<string>();
            for (int i = 0; i < list.Count; i += 2)
            {
                lines.Add(i + 1 < list.Count ? list[i] + "  " + list[i + 1] : list[i]);
            }

            lines.Add("#b 黑方   #m 黑幕");
            return lines;
        }

        private static IEnumerable<string> RoomOptions()
        {
            List<int> pool = ClueFakeLogic.RoomPool();
            var list = new List<string>();
            for (int i = 0; i < pool.Count; i++)
            {
                list.Add("#" + (i + 1) + " " + ClueFakeLogic.RoomNameOf(pool[i]));
            }

            return PackThree(list);
        }

        private static IEnumerable<string> ActivityOptions(int roomId)
        {
            List<EWhatType> options = ClueFakeLogic.ActivitiesOfRoom(roomId);
            var list = new List<string>();
            for (int i = 0; i < options.Count; i++)
            {
                list.Add("#" + (i + 1) + " " + ClueFakeLogic.WhatNameOf(options[i]));
            }

            return PackThree(list);
        }

        /// <summary>每行最多 3 项（与用户给的交互稿一致，控制气泡高度）。</summary>
        private static IEnumerable<string> PackThree(List<string> items)
        {
            var lines = new List<string>();
            for (int i = 0; i < items.Count; i += 3)
            {
                var sb = new global::System.Text.StringBuilder();
                for (int k = i; k < items.Count && k < i + 3; k++)
                {
                    if (sb.Length > 0)
                    {
                        sb.Append("  ");
                    }

                    sb.Append(items[k]);
                }

                lines.Add(sb.ToString());
            }

            return lines;
        }

        // ══════════════════ 发送 ══════════════════

        private static void Reply(
            ServerGameRoom room, ServerGamePlayer player, int deviceId, ChatChannelMask channel, string single)
        {
            Reply(room, player, deviceId, channel, single, null);
        }

        private static void Reply(
            ServerGameRoom room, ServerGamePlayer player, int deviceId, ChatChannelMask channel,
            string single, List<string> lines)
        {
            if (lines == null)
            {
                lines = new List<string> { single ?? string.Empty };
            }

            // 按命令的来源频道回执：黑方秘密终端 → SecretChat；公开/普通聊天 → NormalChat。
            GameChatFeature.ReplyByChannel(room, player, channel, deviceId, null, lines);
        }

        /// <summary>回执里的短时钟：<c>7:49am</c>（无前导 0、无空格，与用户给的交互稿一致）。</summary>
        private static string ShortClockOf(int minutes)
        {
            int hour = minutes / 60;
            int minute = minutes % 60;
            string meridiem = hour < 12 ? "am" : "pm";
            if (hour > 12)
            {
                hour -= 12;
            }

            return hour + ":" + minute.ToString("00") + meridiem;
        }
    }
}
