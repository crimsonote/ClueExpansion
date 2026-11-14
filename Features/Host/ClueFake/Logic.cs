using System.Collections.Generic;
using ClueDetail.Core;
using Protocol;
using ServerChatDevice = Server.Game.ChatDevice;
using ServerDevice = Server.Game.Device;
using ServerDeviceManager = Server.Game.DeviceManager;
using ServerGamePlayer = Server.Game.Player;
using ServerGameRoom = Server.Game.GameRoom;
using ServerTimeManager = Server.Game.TimeManager;

namespace ClueDetail.Features.Host.ClueFake
{
    /// <summary>
    /// 线索伪造的解析、写入与提示逻辑（纯服务端，全部在 Unity 主线程调用）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>时间口径</b>：<c>PropositionInfo.Time</c> 与 <c>TimeManager.SurviveTime</c> 是同一个单位
    /// 「游戏内分钟」—— <c>Util.FormatTimeSentence</c>（0.1.18 :27277）里
    /// <c>小时 = time / 60</c>，开局 7:00am 对应 420，所以 <c>7:49am = 469</c>。
    /// 与原版伪证页 <c>OnWhenDropdownValueChanged</c>（:53730）逐字一致。
    /// </para>
    /// <para>
    /// <b>活动候选</b>：只用**真实设备**（<c>DeviceManager.Objects</c>）按 <c>RoomID</c> 分组，
    /// 而不是 <c>RoomDeviceScan</c> 的静态表（那个按 <c>RoomType</c> 推导，会跨房间）。
    /// </para>
    /// </remarks>
    internal static class ClueFakeLogic
    {
        /// <summary>开局时刻（7:00am）对应的游戏内分钟。</summary>
        internal const int GameStartMinute = 420;

        // ══════════════════ 候选 ══════════════════

        /// <summary>可作为伪证目标的房间：<c>SchoolMain</c> 里存在有效设备的房间（原版 <c>LieRoomSet</c> 口径）。</summary>
        internal static List<int> RoomPool()
        {
            var result = new List<int>();
            var dm = ServerDeviceManager.Instance;
            if (dm == null || dm.Objects == null)
            {
                return result;
            }

            var seen = new HashSet<int>();
            for (int i = 0; i < dm.Objects.Count; i++)
            {
                ServerDevice device = dm.Objects[i];
                if (device == null || device.DeviceData == null)
                {
                    continue;
                }

                int roomId = device.RoomID;
                if (roomId == 0 || seen.Contains(roomId))
                {
                    continue;
                }

                if (!IsTargetDevice(device))
                {
                    continue;
                }

                seen.Add(roomId);
                result.Add(roomId);
            }

            result.Sort();
            return result;
        }

        /// <summary>该房间可被伪造的活动（去重；口径同原版 <c>RecordLastUsingPlayer</c> 的设备筛法）。</summary>
        internal static List<EWhatType> ActivitiesOfRoom(int roomId)
        {
            var result = new List<EWhatType>();
            var dm = ServerDeviceManager.Instance;
            if (dm == null || dm.Objects == null || roomId == 0)
            {
                return result;
            }

            for (int i = 0; i < dm.Objects.Count; i++)
            {
                ServerDevice device = dm.Objects[i];
                if (device == null || device.RoomID != roomId || !IsTargetDevice(device))
                {
                    continue;
                }

                EWhatType what = device.DeviceData.WhatType;
                if (!result.Contains(what))
                {
                    result.Add(what);
                }
            }

            return result;
        }

        /// <summary>目标房间中第一台活动匹配的真实设备。</summary>
        internal static ServerDevice FindDevice(int roomId, EWhatType what)
        {
            var dm = ServerDeviceManager.Instance;
            if (dm == null || dm.Objects == null)
            {
                return null;
            }

            for (int i = 0; i < dm.Objects.Count; i++)
            {
                ServerDevice device = dm.Objects[i];
                if (device != null
                    && device.RoomID == roomId
                    && device.DeviceData != null
                    && device.DeviceData.WhatType == what)
                {
                    return device;
                }
            }

            return null;
        }

        /// <summary>发信机设备（痕迹的落点）。</summary>
        internal static ServerChatDevice FindChatDevice(int deviceId)
        {
            var dm = ServerDeviceManager.Instance;
            return dm == null ? null : dm.GetDeviceById(deviceId) as ServerChatDevice;
        }

        private static bool IsTargetDevice(ServerDevice device)
        {
            EWhatType what = device.DeviceData.WhatType;
            // 与 RoomDeviceScan / 原版 InitBlackPropositionData 同口径：
            // 电闸是角色级动作（切断了电力），不算「房间里某台设备的活动」。
            return what != EWhatType.WNone && what != EWhatType.WFusebox;
        }

        // ══════════════════ 解析：时间 ══════════════════

        /// <summary>
        /// 解析时间参数：<c>07:00am</c> / <c>7:49</c> / <c>7:49pm</c> / <c>469</c>。
        /// </summary>
        internal static bool TryParseClock(string text, out int minutes, out string error)
        {
            minutes = 0;
            error = null;

            string s = (text ?? string.Empty).Trim();
            if (s.Length == 0)
            {
                error = "时间不能为空。";
                return false;
            }

            // 纯数字：直接当游戏内分钟（7:49 = 469）
            int raw;
            if (s.IndexOf(':') < 0 && global::System.Int32.TryParse(s, out raw))
            {
                if (raw < 0 || raw > 1439)
                {
                    error = "时间必须在 0~1439 之间（或写成 7:49am）。";
                    return false;
                }

                minutes = raw;
                return true;
            }

            bool pm = s.EndsWith("pm", global::System.StringComparison.OrdinalIgnoreCase);
            bool am = s.EndsWith("am", global::System.StringComparison.OrdinalIgnoreCase);
            if (pm || am)
            {
                s = s.Substring(0, s.Length - 2).Trim();
            }

            string[] parts = s.Split(':');
            int hour;
            int minute;
            if (parts.Length != 2
                || !global::System.Int32.TryParse(parts[0], out hour)
                || !global::System.Int32.TryParse(parts[1], out minute))
            {
                error = "时间格式应为 07:00am/pm、21:49（24 小时制），或直接给游戏内分钟（如 469）。";
                return false;
            }

            if (minute < 0 || minute > 59)
            {
                error = "分钟必须在 0~59 之间。";
                return false;
            }

            if (pm || am)
            {
                if (hour < 1 || hour > 12)
                {
                    error = "带 am/pm 时小时必须在 1~12 之间。";
                    return false;
                }

                int hour24 = hour;
                if (pm && hour != 12)
                {
                    hour24 += 12;
                }
                else if (am && hour == 12)
                {
                    hour24 = 0;
                }

                minutes = hour24 * 60 + minute;
                return true;
            }

            // 不带 am/pm ⇒ 按 24 小时制（21:49 = 晚 9:49）。
            if (hour < 0 || hour > 23)
            {
                error = "24 小时制的小时必须在 0~23 之间。";
                return false;
            }

            minutes = hour * 60 + minute;
            return true;
        }

        /// <summary>当前游戏时间（分钟，07:00 = 420 + 已生存分钟）。</summary>
        internal static int NowMinutes()
        {
            var tm = ServerTimeManager.Instance;
            int survive = tm == null ? 0 : tm.SurviveTime;
            if (survive < 0)
            {
                survive = 0;
            }

            return GameStartMinute + survive / 60;
        }

        /// <summary>可写入的线索时间范围：07:00 ~ 23:59（允许写到「现在」之后，伪证本来就是伪造的）。</summary>
        internal static bool IsWritableClock(int minutes, out string error)
        {
            error = null;

            if (minutes < GameStartMinute)
            {
                error = "时间不能早于开局（7:00am）。";
                return false;
            }

            if (minutes > 23 * 60 + 59)
            {
                error = "时间不能晚于 23:59。";
                return false;
            }

            return true;
        }

        // ══════════════════ 解析：人物 ══════════════════

        /// <summary>解析人物：昵称 / <c>#id</c> / 数字 id / <c>b</c>（黑）/ <c>m</c>（黑幕）。</summary>
        internal static bool TryResolvePerson(ServerGameRoom room, string text, out int playerId, out string error)
        {
            playerId = 0;
            error = null;

            string s = (text ?? string.Empty).Trim();
            if (s.StartsWith("#", global::System.StringComparison.Ordinal))
            {
                s = s.Substring(1).Trim();
            }

            if (s.Length == 0)
            {
                error = "人物不能为空。";
                return false;
            }

            if (s.Equals("b", global::System.StringComparison.OrdinalIgnoreCase))
            {
                ServerGamePlayer black = FindBlack(room);
                if (black == null)
                {
                    error = "本局没有黑方（黑）。";
                    return false;
                }

                playerId = black.PublicInfo.PlayerId;
                return true;
            }

            if (s.Equals("m", global::System.StringComparison.OrdinalIgnoreCase))
            {
                ServerGamePlayer master = FindMastermind(room);
                if (master == null)
                {
                    error = "本局没有黑幕。";
                    return false;
                }

                playerId = master.PublicInfo.PlayerId;
                return true;
            }

            int id;
            if (global::System.Int32.TryParse(s, out id))
            {
                ServerGamePlayer byId = FindById(room, id);
                if (byId == null)
                {
                    error = "房间里没有 #" + id + "。";
                    return false;
                }

                playerId = id;
                return true;
            }

            ServerGamePlayer fuzzy = null;
            for (int i = 0; room.Players != null && i < room.Players.Count; i++)
            {
                ServerGamePlayer p = room.Players[i];
                if (p == null || p.PublicInfo == null || string.IsNullOrEmpty(p.Name))
                {
                    continue;
                }

                if (p.Name.Equals(s, global::System.StringComparison.OrdinalIgnoreCase))
                {
                    playerId = p.PublicInfo.PlayerId;
                    return true;
                }

                if (fuzzy == null && p.Name.IndexOf(s, global::System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    fuzzy = p;
                }
            }

            if (fuzzy != null)
            {
                playerId = fuzzy.PublicInfo.PlayerId;
                return true;
            }

            error = "找不到玩家「" + text + "」。";
            return false;
        }

        internal static ServerGamePlayer FindById(ServerGameRoom room, int playerId)
        {
            for (int i = 0; room.Players != null && i < room.Players.Count; i++)
            {
                ServerGamePlayer p = room.Players[i];
                if (p != null && p.PublicInfo != null && p.PublicInfo.PlayerId == playerId)
                {
                    return p;
                }
            }

            return null;
        }

        /// <summary>黑（杀手）：<c>EPlayerColor.Black</c>。⚠️ 黑幕是 <c>Dark</c>，不要混。</summary>
        internal static ServerGamePlayer FindBlack(ServerGameRoom room)
        {
            for (int i = 0; room.Players != null && i < room.Players.Count; i++)
            {
                ServerGamePlayer p = room.Players[i];
                if (p != null && p.PublicInfo != null && p.Color == EPlayerColor.Black)
                {
                    return p;
                }
            }

            return null;
        }

        /// <summary>黑幕：<c>GameRoom.MasterMind</c>，退到 <c>EPlayerColor.Dark</c>。</summary>
        internal static ServerGamePlayer FindMastermind(ServerGameRoom room)
        {
            if (room.MasterMind != null)
            {
                return room.MasterMind;
            }

            for (int i = 0; room.Players != null && i < room.Players.Count; i++)
            {
                ServerGamePlayer p = room.Players[i];
                if (p != null && p.PublicInfo != null && p.Color == EPlayerColor.Dark)
                {
                    return p;
                }
            }

            return null;
        }

        // ══════════════════ 解析：房间 / 活动 ══════════════════

        /// <summary>解析房间：<c>RoomDic</c> 的编号，或房间显示名（精确 → 包含）。</summary>
        internal static bool TryResolveRoom(string text, out int roomId, out string error)
        {
            roomId = 0;
            error = null;

            string s = (text ?? string.Empty).Trim();
            if (s.StartsWith("#", global::System.StringComparison.Ordinal))
            {
                s = s.Substring(1).Trim();
            }

            if (s.Length == 0)
            {
                error = "房间不能为空。";
                return false;
            }

            int index;
            if (global::System.Int32.TryParse(s, out index))
            {
                // 编号与交互式列表一致：都是 RoomPool() 的 1 基序号（不是 RoomDic 的 DataId）。
                List<int> pool = RoomPool();
                if (index < 1 || index > pool.Count)
                {
                    error = "房间编号必须在 1~" + pool.Count + " 之间。";
                    return false;
                }

                roomId = pool[index - 1];
                return true;
            }

            List<int> rooms = RoomPool();
            int fuzzy = 0;
            for (int i = 0; i < rooms.Count; i++)
            {
                string name = RoomNameOf(rooms[i]);
                if (name.Equals(s, global::System.StringComparison.OrdinalIgnoreCase))
                {
                    roomId = rooms[i];
                    return true;
                }

                if (fuzzy == 0 && name.IndexOf(s, global::System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    fuzzy = rooms[i];
                }
            }

            if (fuzzy != 0)
            {
                roomId = fuzzy;
                return true;
            }

            error = "找不到房间「" + text + "」。";
            return false;
        }

        /// <summary>解析活动：候选列表的序号（1 起）或活动显示名。</summary>
        internal static bool TryResolveActivity(int roomId, string text, out EWhatType what, out string error)
        {
            what = EWhatType.WNone;
            error = null;

            List<EWhatType> options = ActivitiesOfRoom(roomId);
            if (options.Count == 0)
            {
                error = "该房间没有可伪造的活动。";
                return false;
            }

            string s = (text ?? string.Empty).Trim();
            if (s.StartsWith("#", global::System.StringComparison.Ordinal))
            {
                s = s.Substring(1).Trim();
            }

            int index;
            if (global::System.Int32.TryParse(s, out index))
            {
                if (index < 1 || index > options.Count)
                {
                    error = "活动编号必须在 1~" + options.Count + " 之间。";
                    return false;
                }

                what = options[index - 1];
                return true;
            }

            for (int i = 0; i < options.Count; i++)
            {
                if (WhatNameOf(options[i]).Equals(s, global::System.StringComparison.OrdinalIgnoreCase))
                {
                    what = options[i];
                    return true;
                }
            }

            error = "找不到活动「" + text + "」。";
            return false;
        }

        // ══════════════════ 写入 / 痕迹 ══════════════════

        /// <summary>
        /// 把一条伪证写进目标房间设备的 <c>ClueList</c> 并广播。
        /// </summary>
        /// <remarks>
        /// 写入顺序与原版 <c>RecordLastUsingPlayer</c> 一致：先改**服务端真值**，再广播**克隆副本**。
        /// 活动走未知字段 100（见 <see cref="UnknownFields"/>），未装本模块的客户端会忽略它，
        /// 行为与原版一致。
        /// </remarks>
        internal static bool TryWriteFake(
            ServerGameRoom room, int playerId, int time, int roomId, EWhatType what, out string error)
        {
            error = null;

            ServerDevice device = FindDevice(roomId, what);
            if (device == null)
            {
                error = "目标房间没有对应活动的设备。";
                return false;
            }

            if (device.ClueList == null)
            {
                error = "目标设备的线索表不可用。";
                return false;
            }

            // 同键去重：与客户端 AlreadyExistClue（0.1.18 :27607）同口径。
            for (int i = 0; i < device.ClueList.Count; i++)
            {
                PropositionInfo exist = device.ClueList[i];
                if (exist != null
                    && !exist.Destroyed
                    && exist.PlayerId == playerId
                    && exist.Time == time
                    && exist.RoomId == roomId)
                {
                    error = "这台设备上已经有同一条线索了。";
                    return false;
                }
            }

            var info = new PropositionInfo
            {
                PlayerId = playerId,
                Time = time,
                RoomId = roomId,
            };

            // ⚠️ 必须早于 Clone：广播与后续扫描都靠未知字段带上活动。
            UnknownFields.SetWhat(info, (int)what);

            // ⚠️ 先登记伪证键再广播：x 重写的 Broadcast Prefix 会查这个键，
            // 用来区分「我们刚写的伪证」与「真实设备线索」（前者不参与重写）。
            ClueFakeState.RememberFake(playerId, time, roomId);

            device.ClueList.Add(info);
            room.Broadcast(new S_ADD_CLUE
            {
                DeviceId = device.ID,
                Info = info.Clone(),
            });

            global::ClueDetail.Core.Log.Info(
                "线索伪造：已录入虚假线索 [" + playerId + " / " + ClockOf(time) + " / 房间 " + roomId + " / " + what + "]。");
            return true;
        }

        /// <summary>
        /// 在原版发信机上留下「发送了消息」的线索。
        /// </summary>
        /// <remarks>
        /// 直接复用 <c>ChatDevice.RecordMessageClue</c>（0.1.18 :165172）→
        /// <c>Device.RecordLastUsingPlayer</c>（:166386）：它会写 <c>ClueList</c>、广播
        /// <c>S_ADD_CLUE</c>、给操作者发 <c>S_LEFT_CLUE</c>，并受 30 秒合并窗口管理
        /// （本模块的 <c>ClueMerge</c> 接管后按配置执行）。所以痕迹**不是**我们另造的对象，
        /// 而是原版语义里"发信机使用过"的那一条。
        /// </remarks>
        internal static void RecordTrace(ServerChatDevice chat, ServerGamePlayer player, ServerGameRoom room)
        {
            if (chat == null || player == null || room == null || chat.ClueList == null)
            {
                return;
            }

            // 匿名：黑 → 66613（WhoBlack），黑幕 → 11037（WhoMastermind）。
            // 判据按 AGENTS.md §4.9：Black = 黑（杀手）、Dark = 黑幕。
            int roleId = player.Color == EPlayerColor.Black
                ? Define.BLACK_DESTROY_ID          // 66613
                : Define.MASTERMIND_SABOTAGE_ID;   // 11037

            int now = ServerTimeManager.Instance == null ? 0 : ServerTimeManager.Instance.SurviveTime;
            int limit = now / 30 * 30;             // 与原版 30 秒合并窗口同口径

            PropositionInfo exist = null;
            for (int i = 0; i < chat.ClueList.Count; i++)
            {
                PropositionInfo x = chat.ClueList[i];
                if (x != null && !x.Destroyed && x.PlayerId == roleId && x.Time >= limit)
                {
                    exist = x;
                    break;
                }
            }

            if (exist != null)
            {
                PropositionInfo old = exist.Clone();
                exist.Time = now;
                UnknownFields.SetWhat(exist, (int)EWhatType.WChat);
                room.Broadcast(new S_REMOVE_CLUE { DeviceId = chat.ID, Info = old });
                room.Broadcast(new S_ADD_CLUE { DeviceId = chat.ID, Info = exist.Clone() });
            }
            else
            {
                var info = new PropositionInfo
                {
                    PlayerId = roleId,
                    Time = now,
                    RoomId = chat.RoomID,
                };
                // ⚠️ 必须早于 Clone：角色线索的句式仍带 {what}，靠字段 100 才能显示「留下了消息」。
                UnknownFields.SetWhat(info, (int)EWhatType.WChat);
                chat.ClueList.Add(info);
                room.Broadcast(new S_ADD_CLUE { DeviceId = chat.ID, Info = info.Clone() });
            }

            player.Session.Send(new S_LEFT_CLUE { RoleFace = true });
        }

        /// <summary>
        /// <c>x</c> 槽的应用点：改写将要广播出去的 <c>S_ADD_CLUE</c> 的时间与人物。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 挂在 <c>GameRoom.Broadcast</c> 的 Prefix（见 <c>ClueFake/Patch.cs</c>），
        /// 所以无论 <c>RecordLastUsingPlayer</c> 是否被 <c>ClueMerge</c> 接管都有效。
        /// 包内 <c>Info</c> 是克隆副本，服务端 <c>ClueList</c> 里是原件 —— **两处都要改**，
        /// 否则扫描/后续合并仍看到旧值。
        /// </para>
        /// <para>
        /// ⚠️ 角色线索（11037/11038/66613）与销毁证据线索**不参与重写**：它们的文案由原版角色
        /// 分支固定，改了会得到一个自相矛盾的句子。遇到这类线索只跳过、**不清槽**，继续等下一条。
        /// ⚠️ 过期清槽只记一行日志；槽本身是一次性的（命中即清）。
        /// </para>
        /// </remarks>
        internal static void ApplyRewrite(S_ADD_CLUE add)
        {
            if (!FeatureLoader.Enabled<ClueFakeFeature>())
            {
                return;
            }

            ClueFakeState.RewriteSlot slot = ClueFakeState.Rewrite;
            if (!slot.Armed)
            {
                return;
            }

            float now = global::UnityEngine.Time.realtimeSinceStartup;
            if (slot.ExpireAt > 0f && now >= slot.ExpireAt)
            {
                ClueFakeState.AdvanceRewrite();
                global::ClueDetail.Core.Log.Info("线索伪造：x 重写槽已过期，未命中任何线索。");
                return;
            }

            if (add == null || add.Info == null)
            {
                return;
            }

            int oldPlayer = add.Info.PlayerId;
            int oldTime = add.Info.Time;
            int roomId = add.Info.RoomId;

            // 角色线索不参与（保留槽，等下一条普通线索）。
            if (Define.IsRoleClueId(oldPlayer))
            {
                return;
            }

            // 只改**发起者自己**的下一条线索：黑幕挂的槽不能把别人的记录改写成黑。
            // 别人的线索照常广播、不消耗槽（槽继续等发起者）。
            if (oldPlayer != slot.OwnerId)
            {
                return;
            }

            // 我们自己刚写的伪证不参与（否则 x 会吃掉伪造操作本身）。
            if (ClueFakeState.IsFake(oldPlayer, oldTime, roomId))
            {
                return;
            }

            // MarkOnly（/f x x x）：不改任何内容，只把这条**真实**线索标记为伪造。
            if (slot.MarkOnly)
            {
                ClueFakeState.RememberFake(oldPlayer, oldTime, roomId);
                ClueFakeState.AdvanceRewrite();
                global::ClueDetail.Core.Log.Info(
                    "线索伪造：x 标记了一条真实线索为伪造（内容未改）[" + oldPlayer + " / "
                    + ClockOf(oldTime) + " / 房间 " + roomId + "]。");
                return;
            }

            int newPlayer = slot.RealPerson ? oldPlayer : slot.PersonId;

            int newTime;
            if (slot.HasTimeOffset)
            {
                // x 模式的时间偏移：基于**下一条线索自己的真实时间**，而不是命令执行时刻。
                newTime = oldTime + slot.TimeOffset;
                while (newTime < 0) { newTime += 1440; }
                while (newTime > 1439) { newTime -= 1440; }
            }
            else
            {
                newTime = slot.RealTime ? oldTime : slot.Time;
            }

            add.Info.PlayerId = newPlayer;
            add.Info.Time = newTime;

            // ⚠️ 改键之后 <c>RecordDetail</c> 的 <c>StampWhat</c> 按旧键就查不到旁路表了，
            // 所以这里顺手把设备的 WhatType 直接补进包内副本与服务端对象（幂等：已有值不覆盖）。
            int what = 0;

            // 服务端真值（按旧键找；找到就改，找不到只记日志）
            bool truth = false;
            var dm = ServerDeviceManager.Instance;
            ServerDevice device = dm == null ? null : dm.GetDeviceById(add.DeviceId);
            if (device != null && device.DeviceData != null)
            {
                what = (int)device.DeviceData.WhatType;
            }

            if (device != null && device.ClueList != null)
            {
                for (int i = 0; i < device.ClueList.Count; i++)
                {
                    PropositionInfo info = device.ClueList[i];
                    if (info != null
                        && info.PlayerId == oldPlayer
                        && info.Time == oldTime
                        && info.RoomId == roomId)
                    {
                        if (!slot.RealPerson) { info.PlayerId = newPlayer; }
                        if (!slot.RealTime) { info.Time = newTime; }
                        if (what != 0 && UnknownFields.GetWhat(info) == 0)
                        {
                            UnknownFields.SetWhat(info, what);
                        }

                        truth = true;
                        break;
                    }
                }
            }

            if (what != 0 && UnknownFields.GetWhat(add.Info) == 0)
            {
                UnknownFields.SetWhat(add.Info, what);
            }


            ClueFakeState.AdvanceRewrite();

            // x 改写出来的线索同样是伪造：按**改写后的键**登记，/clue 才能标 [伪造]。
            ClueFakeState.RememberFake(newPlayer, newTime, roomId);

            global::ClueDetail.Core.Log.Info(
                "线索伪造：x 改写了一条线索 [" + oldPlayer + " / " + ClockOf(oldTime) + " → "
                + newPlayer + " / " + ClockOf(newTime) + " / 房间 " + roomId + "]"
                + (truth ? "（服务端真值已同步）" : "（⚠️ 服务端真值未找到，可能只改了广播副本）") + "。");
        }

        // ══════════════════ 配额 ══════════════════

        internal static bool TryConsumeQuota(ServerGameRoom room, ServerGamePlayer player, out string error)
        {
            error = null;
            int max = ClueFakeFeature.MaxPerRoundValue;
            bool team = ClueFakeFeature.TeamQuota;

            if (max <= 0 || ClueFakeState.Remaining(room, player, max, team) <= 0)
            {
                error = team ? "本阵营这一局的伪装次数已用完。" : "你这一局的伪装次数已用完。";
                return false;
            }

            ClueFakeState.Consume(room, player, team);
            return true;
        }

        internal static string RemainingText(ServerGameRoom room, ServerGamePlayer player)
        {
            int max = ClueFakeFeature.MaxPerRoundValue;
            int remain = ClueFakeState.Remaining(room, player, max, ClueFakeFeature.TeamQuota);
            return remain + "/" + max;
        }

        // ══════════════════ 展示 ══════════════════

        /// <summary>游戏内时钟（与 <c>Util.FormatTimeSentence</c> 同口径）。</summary>
        internal static string ClockOf(int minutes)
        {
            int hour = minutes / 60;
            int minute = minutes % 60;
            string meridiem = hour < 12 ? "am" : "pm";
            if (hour > 12)
            {
                hour -= 12;
            }

            return hour.ToString("00") + ":" + minute.ToString("00") + " " + meridiem;
        }

        /// <summary>房间显示名（与客户端线索行同一套：RoomDic → ERoomType 名 → GetText）。</summary>
        internal static string RoomNameOf(int roomId)
        {
            try
            {
                Data.RoomData data;
                if (Managers.Data.RoomDic.TryGetValue(roomId, out data) && data != null)
                {
                    string key = global::System.Enum.GetName(typeof(ERoomType), data.Type);
                    string text = Managers.GetText(key);
                    return string.IsNullOrEmpty(text) || text == key ? key : text;
                }
            }
            catch
            {
                // 取不到就退回房间号
            }

            return "房间" + roomId;
        }

        /// <summary>活动显示名（优先本模块注入的中文词条；服务端语言表里 0.1.18 已删掉 W*）。</summary>
        internal static string WhatNameOf(EWhatType what)
        {
            string key = what.ToString();

            // 0.1.18 把 49 条 W* 词条从语言表整体删了（客户端靠 Localization.Ensure 注入，
            // 服务端没有注入），所以服务端的 Managers.GetText 取不到中文 —— 先查自带表。
            Dictionary<string, string> entry;
            if (LocalizationData.Entries != null
                && LocalizationData.Entries.TryGetValue(key, out entry)
                && entry != null)
            {
                string chs;
                if (entry.TryGetValue("CHS", out chs) && !string.IsNullOrEmpty(chs))
                {
                    return chs;
                }
            }

            try
            {
                string text = Managers.GetText(key);
                return string.IsNullOrEmpty(text) || text == key ? key : text;
            }
            catch
            {
                return key;
            }
        }

        /// <summary>人物显示名（昵称，找不到退回 #id）。</summary>
        internal static string PersonNameOf(ServerGameRoom room, int playerId)
        {
            ServerGamePlayer p = FindById(room, playerId);
            if (p != null && !string.IsNullOrEmpty(p.Name))
            {
                return p.Name;
            }

            return "#" + playerId;
        }
    }
}
