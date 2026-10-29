using System.Collections.Generic;
using ClueDetail.Core;
using HarmonyLib;
using Protocol;
using ServerDevice = Server.Game.Device;
using ServerDeviceManager = Server.Game.DeviceManager;
using ServerGamePlayer = Server.Game.Player;
using ServerGameRoom = Server.Game.GameRoom;
using BreakerDevice = ClueDetail.Features.Host.ShadowSabotage.BreakerDevice;

namespace ClueDetail.Features.Host.GameChat
{
    internal partial class GameChatFeature
    {
        /// <summary>一条"被某个玩家搜集到"的线索快照。</summary>
        private sealed class ClueRow
        {
            public int PlayerId;
            public int Time;        // 显示时刻优先（污染冻结值），否则真实 Time
            public int RoomId;
            public int What;        // EWhatType；0 = 未知
            public int RealOwner;   // 未知字段 103：身份污染时的真实当事人（0 = 无）
            public int DeviceId;    // 线索所属设备（销毁证据时用来补设备名）
            public string Text;     // 按**观察者**渲染的一行（查询时填，见 Render）
            public string Key;      // 去重键（玩家:时间:房间）

            /// <summary>
            /// 这条线索来自哪个破坏点：-1 = 普通设备，0 = 身份识别器，1 = 时钟。
            /// </summary>
            /// <remarks>临时判例（2026-10-20），只为 /clue 的文本服务，可随判例一起整体移除。</remarks>
            public int Breaker = -1;
        }

        /// <summary>逐人线索表：<c>pid → (线索键 → 快照)</c>。</summary>
        /// <remarks>
        /// 线索键 = <c>玩家:时间:房间</c>，与客户端列表口径一致（<c>AlreadyExistClue</c> :27611）。
        /// 只在**扫描**时记：服务端 <c>DeviceManager.Scan</c>（:166680）会把该设备的线索只发给扫描者，
        /// 所以"谁扫过"就是"谁搜集到"。
        /// </remarks>
        private static readonly Dictionary<int, Dictionary<string, ClueRow>> Index =
            new Dictionary<int, Dictionary<string, ClueRow>>();

        /// <summary>
        /// 线索互通时补登记：被同步的一方没扫过这台设备，但 /clue 里应该能看到这条。
        /// </summary>
        internal static void RememberShared(ServerGamePlayer player, int deviceId)
        {
            RememberScan(ServerDeviceManager.Instance, player, deviceId);
        }
        /// <summary>扫描时登记：把该设备当前的线索记到扫描者名下。</summary>
        private static void RememberScan(ServerDeviceManager dm, ServerGamePlayer player, int deviceId)
        {
            if (dm == null || dm.Objects == null || player == null || player.PublicInfo == null)
            {
                return;
            }

            ServerDevice device = null;
            for (int i = 0; i < dm.Objects.Count; i++)
            {
                if (dm.Objects[i] != null && dm.Objects[i].ID == deviceId)
                {
                    device = dm.Objects[i];
                    break;
                }
            }

            if (device == null || device.ClueList == null)
            {
                return;
            }

            var rows = EnsureRows(player);
            for (int i = 0; i < device.ClueList.Count; i++)
            {
                AddRow(rows, device, device.ClueList[i]);
            }
        }

        /// <summary>
        /// 只登记**实际转发给收件人**的那一条线索。
        /// </summary>
        /// <remarks>
        /// 一级（调查阶段补发队友线索）只发同伴产生的那几条；若这里按设备登记，
        /// 会把同一设备上白方玩家的线索也塞进收件人的 <c>/c</c> 索引，等于多给了信息。
        /// 二级扫描本来就是整台设备，仍使用按设备登记的入口。
        /// </remarks>
        internal static void RememberShared(ServerGamePlayer player, ServerDevice device, PropositionInfo info)
        {
            if (player == null || player.PublicInfo == null || device == null || info == null || info.Destroyed)
            {
                return;
            }

            AddRow(EnsureRows(player), device, info);
        }

        private static Dictionary<string, ClueRow> EnsureRows(ServerGamePlayer player)
        {
            int pid = player.PublicInfo.PlayerId;
            if (!Index.TryGetValue(pid, out var rows))
            {
                rows = new Dictionary<string, ClueRow>();
                Index[pid] = rows;
            }

            return rows;
        }

        private static void AddRow(Dictionary<string, ClueRow> rows, ServerDevice device, PropositionInfo info)
        {
            if (info == null || info.Destroyed)
            {
                return;
            }

            int breaker = device is BreakerDevice breakerDevice ? (int)breakerDevice.Kind : -1;
            int what = device.DeviceData != null ? (int)device.DeviceData.WhatType : 0;
            int display = ClueDetail.Core.UnknownFields.GetDisplayTime(info);
            var row = new ClueRow
            {
                PlayerId = info.PlayerId,
                Time = display != 0 ? display : info.Time,
                RoomId = info.RoomId,
                What = what,
                Breaker = breaker,
                DeviceId = device.ID,
                RealOwner = ClueDetail.Core.UnknownFields.GetRealOwner(info),
            };
            row.Key = KeyOf(info.PlayerId, info.Time, info.RoomId);
            rows[row.Key] = row;
        }

        /// <summary>换局/阶段切换时清空（线索不跨局）。</summary>
        internal static void Clear()
        {
            Index.Clear();
        }

        private static string KeyOf(int playerId, int time, int roomId)
        {
            return playerId + ":" + time + ":" + roomId;
        }

        /// <summary>按**观察者**渲染一行：<c>07:46am · 广播室 · 某人 · 点燃了蜡烛</c>。</summary>
        /// <remarks>
        /// ⚠️ 不能在登记时就渲染死：身份污染把真值 <c>PlayerId</c> 写成 0，
        /// 但**当事人自己**看这条应当显示实名（与出站口「按收件人」同一口径）。
        /// 真实当事人取自未知字段 103（<see cref="ClueDetail.Core.UnknownFields.RealOwnerFieldNumber"/>）。
        /// </remarks>
        private static string Render(ClueRow row, int viewerId)
        {
            string who = WhoOf(row.PlayerId, row.RealOwner, viewerId);
            string room = RoomNameOf(row.RoomId);
            string act = ActOf(row.PlayerId, row.What, row.Breaker, row.DeviceId);
            string when = ClockOf(row.Time);

            // 与官方文本消息同句式：`{谁}在{时间}于{房间}{动作}`；
            // 销毁线索多一段「{设备}处」（官方只到房间，这是 /clue 的增强）。
            if (IsDestroyRow(row) && TryDeviceName(row.DeviceId, out string dev))
            {
                return $"{who}在{when}于{room}{dev}处{act}";
            }

            return $"{who}在{when}于{room}{act}";
        }

        /// <summary>
        /// 人：真实玩家用昵称；11037/11038/66613 = 角色线索；
        /// <c>PlayerId == 0</c>（身份污染）时，**只有当事人自己**看到实名，其他人看「某人」。
        /// </summary>
        private static string WhoOf(int playerId, int realOwner, int viewerId)
        {
            // 身份污染（2026-10-20 起）：「某人」档**不再改写真值 PlayerId**，
            // 改由字段 103 标记「这条产生于身份破坏期」，真值里仍是被记录的那个人。
            // 所以判据从 playerId == 0 换成 realOwner != 0：
            //   · 当事人自己 ⇒ 实名（他自己的线索不该被匿名）；
            //   · 其他人     ⇒ 按占位符显示（真值被改成 66613/11037 就显示角色名，否则「某人」）。
            if (realOwner != 0)
            {
                if (realOwner == viewerId)
                {
                    return NameOf(realOwner);
                }

                return playerId != realOwner ? DisplayOf(playerId) : Managers.GetText("WhoSomeone");
            }

            return DisplayOf(playerId);
        }

        /// <summary>纯展示层：按 PlayerId 给出「黑幕 / 黑 / 昵称 / 某人」。</summary>
        private static string DisplayOf(int playerId)
        {
            if (playerId == 0)
            {
                return Managers.GetText("WhoSomeone");     // 兼容旧数据（真值仍写着 0）
            }

            if (Define.IsMastermindClueId(playerId))
            {
                return Managers.GetText("WhoMastermind");
            }

            if (playerId == Define.BLACK_DESTROY_ID || playerId == Define.MASTERMIND_DESTROY_ID)
            {
                return Managers.GetText("WhoBlack");
            }

            return NameOf(playerId);
        }

        /// <summary>座位号 → 昵称（查不到就退回数字）。</summary>
        private static string NameOf(int playerId)
        {
            var room = ServerGameRoom.Instance;
            if (room != null && room.Players != null)
            {
                for (int i = 0; i < room.Players.Count; i++)
                {
                    var p = room.Players[i];
                    if (p != null && p.PublicInfo != null && p.PublicInfo.PlayerId == playerId)
                    {
                        return p.Name ?? playerId.ToString();
                    }
                }
            }

            return playerId.ToString();
        }

        /// <summary>活动：角色线索走原版两选一，其余走设备活动词条。</summary>
        private static string ActOf(int playerId, int what, int breaker = -1, int deviceId = 0)
        {
            // ══════════════════ 临时判例（2026-10-20，可整体移除）══════════════════
            // 黑幕关闭破坏点时，/clue 里显示它具体关了什么，而不是笼统的「切断了电力」。
            // ⚠️ 只影响本命令的输出：线索真值、广播、以及未装本模块的客户端看到的
            // 依旧是 WhatCutPower（原版口径），所以不存在兼容性问题。
            if (breaker == 0)
            {
                return "关闭了身份识别器";
            }

            if (breaker == 1)
            {
                return "关闭了时间服务器";
            }
            // ══════════════════════════════════════════════════════════════════

            if (playerId == Define.MASTERMIND_SABOTAGE_ID)
            {
                return Managers.GetText(Define.WHAT_CUT_POWER);
            }

            if (Define.IsDestroyEvidenceClue(new PropositionInfo { PlayerId = playerId }))
            {
                return Managers.GetText(Define.WHAT_DESTROY_EVIDENCE);
            }

            return what == 0 ? string.Empty : WhatNameOf(what);
        }

        private static bool IsDestroyRow(ClueRow row)
        {
            return row != null && Define.IsDestroyEvidenceClue(new PropositionInfo { PlayerId = row.PlayerId });
        }

        private static bool TryDeviceName(int deviceId, out string name)
        {
            name = null;
            EWhatType dev = ClueDetail.Core.ActivityMap.WhatOfDevice(deviceId);
            if (dev == EWhatType.WNone)
            {
                return false;
            }

            string text = Managers.GetText(dev.ToString());
            if (string.IsNullOrEmpty(text) || text == dev.ToString())
            {
                return false;       // 缺词条 ⇒ 不拼英文 key
            }

            name = text;
            return true;
        }
        // ══════════════════ 搜索 ══════════════════

        /// <summary>
        /// 解析条件并返回该玩家"搜集到的"线索（已按时间升序）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// **三类来源合并**（2026-10-19 用户定案）：
        /// </para>
        /// <list type="number">
        /// <item><b>自己扫过的</b>：<see cref="RememberScan"/> 在扫描时登记；</item>
        /// <item><b>关于自己的</b>：<c>clue.PlayerId == 自己</c> 的记录 —— 原版在有人操作设备时会
        /// 给当事人发 <c>S_LEFT_CLUE{RoleFace}</c>（自己的"角色脸"），当事人因此也"知道"这条；
        /// 这里直接从服务端真值里捞，**不依赖他有没有扫过**（进调查阶段自动就有）；</item>
        /// <item><b>死亡 / 旁观者：全部线索</b> —— 死者的信息面本来就等于全场（他们能看所有人）。</item>
        /// </list>
        /// <para>
        /// ⚠️ 三类之间按 <c>玩家:时间:房间</c> 去重，口径与客户端 <c>AlreadyExistClue</c>（:27611）一致。
        /// </para>
        /// </remarks>
        private static List<ClueRow> Search(ServerGamePlayer player, string query)
        {
            var result = new List<ClueRow>();
            if (player == null || player.PublicInfo == null)
            {
                return result;
            }

            int pid = player.PublicInfo.PlayerId;
            var seen = new HashSet<string>();

            // ③ 死亡 / 旁观者 ⇒ 全部线索
            bool seesAll = !player.IsAlive || player.IsSpectator;
            if (seesAll)
            {
                CollectFromDevices(null, seen, result);
            }
            else
            {
                // ① 自己扫过的
                if (Index.TryGetValue(pid, out var rows))
                {
                    foreach (var pair in rows)
                    {
                        if (seen.Add(pair.Value.Key))
                        {
                            result.Add(pair.Value);
                        }
                    }
                }

                // ② 关于自己的（不需要扫过）
                CollectFromDevices(pid, seen, result);
            }

            var conditions = ParseConditions(query);
            var matched = new List<ClueRow>();
            for (int i = 0; i < result.Count; i++)
            {
                ClueRow row = result[i];
                row.Text = Render(row, pid);          // 按**观看者**渲染（当事人看实名）
                if (Matches(row, conditions, pid))
                {
                    matched.Add(row);
                }
            }

            matched.Sort((a, b) => a.Time.CompareTo(b.Time));
            return matched;
        }

        /// <summary>
        /// 从服务端真值（各设备 <c>ClueList</c>）里捞线索。
        /// </summary>
        /// <param name="ownerFilter">非 null 时只取 <c>PlayerId</c> 等于它的记录。</param>
        private static void CollectFromDevices(int? ownerFilter, HashSet<string> seen, List<ClueRow> result)
        {
            var dm = ServerDeviceManager.Instance;
            if (dm == null || dm.Objects == null)
            {
                return;
            }

            for (int i = 0; i < dm.Objects.Count; i++)
            {
                ServerDevice device = dm.Objects[i];
                if (device == null || device.ClueList == null)
                {
                    continue;
                }

                int what = device.DeviceData != null ? (int)device.DeviceData.WhatType : 0;
                for (int j = 0; j < device.ClueList.Count; j++)
                {
                    PropositionInfo info = device.ClueList[j];
                    if (info == null || info.Destroyed)
                    {
                        continue;
                    }

                    if (ownerFilter.HasValue && info.PlayerId != ownerFilter.Value)
                    {
                        continue;
                    }

                    string key = KeyOf(info.PlayerId, info.Time, info.RoomId);
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    int display = ClueDetail.Core.UnknownFields.GetDisplayTime(info);
                    int time = display != 0 ? display : info.Time;
                    result.Add(new ClueRow
                    {
                        PlayerId = info.PlayerId,
                        Time = time,
                        RoomId = info.RoomId,
                        What = what,
                        RealOwner = ClueDetail.Core.UnknownFields.GetRealOwner(info),
                        DeviceId = device.ID,
                        Key = key,
                    });
                }
            }
        }

        private sealed class Conditions
        {
            public int TimeMin = int.MinValue;
            public int TimeMax = int.MaxValue;
            public readonly List<string> Include = new List<string>();
            /// <summary>按玩家 ID 精确匹配（<c>id:N</c>）；同时比对真实当事人（身份污染时的字段 103）。</summary>
            public readonly List<int> Ids = new List<int>();
            public readonly List<string> Exclude = new List<string>();
        }

        private static Conditions ParseConditions(string query)
        {
            var c = new Conditions();
            if (string.IsNullOrWhiteSpace(query))
            {
                return c;
            }

            foreach (string token in SplitTokens(query))
            {
                string t = token.Trim();
                if (t.Length == 0)
                {
                    continue;
                }

                if (t[0] == '-')
                {
                    if (t.Length > 1)
                    {
                        c.Exclude.Add(t.Substring(1));
                    }

                    continue;
                }

                // id:N —— 按玩家 ID **精确**匹配（昵称查不到时 /clue 的 p: 只能模糊匹配，不够用）。
                if (t.Length > 3 && t.StartsWith("id:", global::System.StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(t.Substring(3).Trim(), out int wantId))
                {
                    c.Ids.Add(wantId);
                    continue;
                }

                if (TryParseTimeRange(t, c))
                {
                    continue;
                }

                c.Include.Add(t);
            }

            return c;
        }

        private static IEnumerable<string> SplitTokens(string query)
        {
            var buffer = new global::System.Text.StringBuilder();
            bool quoted = false;
            for (int i = 0; i < query.Length; i++)
            {
                char ch = query[i];
                if (ch == '"')
                {
                    quoted = !quoted;
                    continue;
                }

                if (!quoted && (ch == ' ' || ch == '\t'))
                {
                    if (buffer.Length > 0)
                    {
                        yield return buffer.ToString();
                        buffer.Clear();
                    }

                    continue;
                }

                buffer.Append(ch);
            }

            if (buffer.Length > 0)
            {
                yield return buffer.ToString();
            }
        }

        /// <summary>识别 <c>t:</c> 时间条件（时钟 / 原始秒 / 开区间 / 区间）。</summary>
        private static bool TryParseTimeRange(string token, Conditions c)
        {
            if (!token.StartsWith("t:", global::System.StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string body = token.Substring(2).Trim();
            if (body.Length == 0)
            {
                return true;
            }

            if (body[0] == '>' && ParseClock(body.Substring(1).Trim(), out int gt))
            {
                c.TimeMin = gt + 1;
                return true;
            }

            if (body[0] == '<' && ParseClock(body.Substring(1).Trim(), out int lt))
            {
                c.TimeMax = lt - 1;
                return true;
            }

            int dash = body.IndexOf('-');
            if (dash > 0)
            {
                if (ParseClock(body.Substring(0, dash).Trim(), out int lo) &&
                    ParseClock(body.Substring(dash + 1).Trim(), out int hi))
                {
                    c.TimeMin = lo;
                    c.TimeMax = hi;
                    return true;
                }
            }

            if (ParseClock(body, out int single))
            {
                c.TimeMin = single;
                c.TimeMax = single + 59;      // 单个时刻 = 这一分钟内
            }

            return true;
        }

        /// <summary>解析时刻：<c>420</c>（原始秒）或 <c>7:00am</c> / <c>19:30</c>（游戏内时钟）。</summary>
        private static bool ParseClock(string text, out int seconds)
        {
            seconds = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string s = text.Trim().ToLowerInvariant();
            bool pm = s.EndsWith("pm", global::System.StringComparison.Ordinal);
            bool am = s.EndsWith("am", global::System.StringComparison.Ordinal);
            if (pm || am)
            {
                s = s.Substring(0, s.Length - 2).Trim();
            }

            int colon = s.IndexOf(':');
            if (colon > 0)
            {
                if (!int.TryParse(s.Substring(0, colon), out int h) ||
                    !int.TryParse(s.Substring(colon + 1), out int m))
                {
                    return false;
                }

                if (pm && h < 12)
                {
                    h += 12;
                }

                seconds = h * 60 + m;
                return true;
            }

            return int.TryParse(s, out seconds);
        }

        private static bool Matches(ClueRow row, Conditions c, int viewerId)
        {
            if (c.Ids.Count > 0)
            {
                // ⚠️ 2026-10-20：污染行对**非当事人**不参与 `id:` 匹配。
                // 原判据（`row.PlayerId == N || row.RealOwner == N`）不看检视者，
                // 而渲染层 `WhoOf` 对非当事人一律显示「某人」⇒ 非当事人逐个 id 试就能反查出真身，
                // 身份破坏点等于白做（某人档真值仍是真实 id，嫁祸档则靠字段 103）。
                // 现在与 `WhoOf` 同口径：
                //   · 当事人自己（RealOwner == viewerId）⇒ 照常按真实 id 搜到；
                //   · 其他人在污染期 ⇒ 这条对 `id:` 隐形，想看全部匿名记录用 `p:某人`。
                // 嫁祸档不受影响：`id:11037` / `id:66613` 仍能按占位符命中那些线索。
                bool polluted = row.RealOwner != 0 && row.RealOwner != viewerId;

                // 非当事人看到的编号：某人档的真值就是真实当事人 ⇒ 不可见（-1）；
                // 嫁祸档的真值是占位符（11037 / 66613）⇒ 保留，玩家看得见什么就能搜什么。
                int visibleId = polluted && row.PlayerId == row.RealOwner ? -1 : row.PlayerId;

                bool hit = false;
                for (int i = 0; i < c.Ids.Count; i++)
                {
                    int want = c.Ids[i];
                    bool ok = visibleId == want;

                    // 当事人自己：真实 id 也认（嫁祸档的真值已经被改成占位符）。
                    if (!ok && row.RealOwner != 0 && row.RealOwner == viewerId)
                    {
                        ok = row.RealOwner == want;
                    }

                    if (ok)
                    {
                        hit = true;
                        break;
                    }
                }

                if (!hit)
                {
                    return false;
                }
            }

            if (row.Time < c.TimeMin || row.Time > c.TimeMax)
            {
                return false;
            }

            for (int i = 0; i < c.Exclude.Count; i++)
            {
                if (row.Text.IndexOf(c.Exclude[i], global::System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }

            for (int i = 0; i < c.Include.Count; i++)
            {
                string token = c.Include[i];
                string probe = row.Text;
                char head = token.Length > 1 ? token[0] : '\0';
                if (token.Length > 2 && token[1] == ':')
                {
                    string body = token.Substring(2);
                    switch (head)
                    {
                        case 'p':
                        case 'P':
                            probe = WhoOf(row.PlayerId, row.RealOwner, viewerId);
                            break;
                        case 'r':
                        case 'R':
                            probe = RoomNameOf(row.RoomId);
                            break;
                        case 'a':
                        case 'A':
                        probe = ActOf(row.PlayerId, row.What, row.Breaker);
                            break;
                    }

                    token = body;
                }

                if (probe.IndexOf(token, global::System.StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        // ══════════════════ 钩子：扫描即登记 ══════════════════

        /// <summary>
        /// 登记"谁扫了哪台设备"。挂在 <c>DeviceManager.Scan(Player, int)</c>（0.1.18 :166680），
        /// 那是所有扫描的唯一入口（<c>C_SCAN_DEVICE</c> → :177952）。
        /// </summary>
        /// <remarks>
        /// 为什么不在 <c>Device.Scan(Player)</c> 上挂：那是 <c>virtual</c>，各设备类型可能覆写（如
        /// <c>Armory</c> :164531、<c>Mirror</c> :171957），挂基类收不到派生类的调用；而
        /// <c>DeviceManager.Scan(player, id)</c> 是**唯一的收敛点**。
        /// </remarks>
        [HarmonyPatch(typeof(ServerDeviceManager), "Scan", new[] { typeof(ServerGamePlayer), typeof(int) })]
        internal static class ScanHook
        {
            [HarmonyPostfix]
            private static void Postfix(ServerDeviceManager __instance, ServerGamePlayer player, int id)
            {
                if (!FeatureLoader.Enabled<GameChatFeature>()) return;
                try
                {
                    RememberScan(__instance, player, id);
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "游戏内命令：线索登记失败。");
                }
            }
        }
    }
}