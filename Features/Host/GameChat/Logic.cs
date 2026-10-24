using System.Collections.Generic;
using Protocol;
using ServerDevice = Server.Game.Device;
using ServerDeviceManager = Server.Game.DeviceManager;
using ServerGamePlayer = Server.Game.Player;
using ServerGameRoom = Server.Game.GameRoom;

namespace ClueDetail.Features.Host.GameChat
{
    internal partial class GameChatFeature
    {
        // ══════════════════ 分发 ══════════════════

        /// <summary>命令分发（在主线程 <c>room.Push</c> 里执行）。</summary>
        /// <summary>
        /// 单字母短别名 → 主名（2026-10-20 用户要求：所有局内命令都提供单字母别名）。
        /// </summary>
        /// <remarks>
        /// 在**判断归属之前**就规范化：这样 <c>/c</c> 也算自家命令；完整名照常可用。
        /// 未知命令原样返回（转小写）。
        /// </remarks>
        private static string Canonical(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return name;
            }

            string lower = name.ToLowerInvariant();
            switch (lower)
            {
                case "s": return "say";
                case "c": return "clue";
                case "w": return "who";
                case "h": return "help";
                default: return lower;
            }
        }

        private static void Dispatch(ServerGameRoom room, ServerGamePlayer player, string name, string arg)
        {
            name = Canonical(name);
            switch (name)
            {
                case "say":
                    CmdSay(room, player, arg);
                    break;
                case "clue":
                    CmdClue(room, player, arg);
                    break;
                case "who":
                    CmdWho(room, player);
                    break;
                case "help":
                    CmdHelp(room, player);
                    break;
            }
        }

        /// <summary>本阶段可用的命令清单（给 <c>/help</c> 与错误提示用）。</summary>
        private static string[] AvailableCommands(ServerGameRoom room)
        {
            var list = new List<string>();
            if (room.State == EGameState.Lobby || room.State == EGameState.Trial)
            {
                list.Add("/s <昵称|ID> <消息>  私聊（= /say）");
            }

            if (room.State == EGameState.Trial)
            {
                list.Add("/c [条件...]  搜索线索（= /clue）");
                list.Add("/c help  完整语法（t: p: r: a: id: 等）");
            }


                list.Add("/w  玩家 ID 与昵称（= /who）");
                list.Add("/h  这份帮助（= /help）");
            return list.ToArray();
        }

        // ══════════════════ /say ══════════════════

        /// <summary>/say 私聊。只在<b>大厅与会议</b>阶段可用；发信机频道不参与。</summary>
        private static void CmdSay(ServerGameRoom room, ServerGamePlayer player, string arg)
        {
            if (room.State != EGameState.Lobby && room.State != EGameState.Trial)
            {
                Reply(room, player, "私聊只在大厅与会议阶段可用。");
                return;
            }

            string[] parts = arg.Split(new[] { ' ', '\t' }, global::System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                Reply(room, player, "用法：/say <昵称|玩家ID> <消息>（/who 看 ID）");
                return;
            }

            string who = parts[0];
            string message = arg.Substring(arg.IndexOf(who, global::System.StringComparison.Ordinal) + who.Length).Trim();
            if (message.Length == 0)
            {
                Reply(room, player, "用法：/say <昵称|玩家ID> <消息>（/who 看 ID）");
                return;
            }

            ServerGamePlayer target = FindTarget(room, who);
            if (target == null)
            {
                Reply(room, player, $"找不到玩家「{who}」（可用昵称或座位号）。");
                return;
            }

            if (target == player)
            {
                Reply(room, player, "不能私聊自己。");
                return;
            }

            // ⚠️ 2026-10-24 改（配合 DT 的「生死互通」）：**允许私聊死者**。
            // IsDead 的语义是「**这条消息的作者是死者**」——只有发送者已死才标。
            // 活人发给死者的那条、以及回给活人的回执，都必须保持正常样式，
            // 否则发送者自己的回执也会变成死者腔。
            bool senderDead = !player.IsAlive;

            // 只有双方可见：目标一份、发送者一份回执，其余人一个字节都不发。
            SendChat(room, target, player, $"[私聊] {player.Name}：{message}", senderDead);
            SendChat(room, player, player, $"已向 {target.Name} 发送私聊：{message}", senderDead);
        }

        /// <summary>按昵称（模糊）或座位号找玩家。</summary>
        private static ServerGamePlayer FindTarget(ServerGameRoom room, string who)
        {
            if (room.Players == null || string.IsNullOrEmpty(who))
            {
                return null;
            }

            // 先按座位号精确匹配（纯数字）
            if (int.TryParse(who, out int pid))
            {
                for (int i = 0; i < room.Players.Count; i++)
                {
                    var p = room.Players[i];
                    if (p != null && p.PublicInfo != null && p.PublicInfo.PlayerId == pid)
                    {
                        return p;
                    }
                }
            }

            // 再按昵称模糊匹配（大小写不敏感；先精确后包含）
            ServerGamePlayer fuzzy = null;
            for (int i = 0; i < room.Players.Count; i++)
            {
                var p = room.Players[i];
                string name = p == null ? null : p.Name;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (string.Equals(name, who, global::System.StringComparison.OrdinalIgnoreCase))
                {
                    return p;
                }

                if (fuzzy == null && name.IndexOf(who, global::System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    fuzzy = p;
                }
            }

            return fuzzy;
        }

        // ══════════════════ /clue ══════════════════

        /// <summary>/clue 搜索自己搜集到的线索。**只在会议阶段可用**（与捉迷藏不同）。</summary>
        /// <summary>每页最多多少个消息气泡（每个气泡 1~3 行）。超过就分页。</summary>
        /// <remarks>
        /// 用户口径 2026-10-19：**以 15 个气泡为上限分页** —— 15 个气泡最多 45 行，
        /// 再多就会把聊天框顶爆，所以超出部分用 /clue ... page:N 翻页。
        /// </remarks>
        private const int MaxBubblesPerPage = 15;
        private static void CmdClue(ServerGameRoom room, ServerGamePlayer player, string arg)
        {
            if (room.State != EGameState.Trial)
            {
                Reply(room, player, "线索搜索只在会议阶段可用。");
                return;
            }

            if (player.PublicInfo == null)
            {
                return;
            }

            string query = (arg ?? string.Empty).Trim();
            if (query.Equals("help", global::System.StringComparison.OrdinalIgnoreCase))
            {
                // ⚠️ 2026-10-20（用户口径）：**3 个气泡 × 3 行**，一个气泡一个主题。
                // 早前两种都不合用：3 行挤一个气泡太密（id: 与 page: 没地方写），
                // 4 条单行又太散（占 4 个气泡，每行只讲一句）。
                // 这里交给 Reply → PackBubbles 按 MaxLinesPerMessage=3 自动打包，9 行正好 3 个气泡；
                // 各行宽度按 MaxLineWidth=50 核过（最宽 48），默认配置下不会折行。
                Reply(room, player, null, new List<string>
                {
                    "【/clue 用法】仅会议可用 ｜ 空格 = 同时满足",
                    "/clue t:7:00am-7:30am p:3 r:广播室 a:点燃",
                    "无条件只报总数 ｜ -词 排除 ｜ 条件可任意叠加",
                    "t: 时间段 7:00am-7:30am ｜ 比较 t:>420（单位秒）",
                    "p: 昵称，模糊匹配 ｜ id: 玩家ID，精确相等",
                    "id: 适合昵称重名或生僻字，编号用 /w 查",
                    "r: 房间 ｜ a: 详细活动 ｜ 裸词 = 全文匹配",
                    "例：/clue r:电脑室 -被 ｜ /clue a:点燃 p:3",
                    "翻页：在查询后加 page:2 ｜ 语法：/c help",
                });
                return;
            }

            // 剥离分页参数 page:N（它不参与搜索）
            int page = 1;
            var kept = new List<string>();
            foreach (string tk in SplitTokens(query))
            {
                if (tk.StartsWith("page:", global::System.StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(tk.Substring(5), out int pg))
                {
                    page = global::System.Math.Max(1, pg);
                    continue;
                }

                kept.Add(tk);
            }

            query = string.Join(" ", kept.ToArray());

            // ⚠️ 用户口径（2026-10-19）：**单输入命令不许全量**（正常一局线索可达上百条，必然刷屏）；
            // 但只要**给了任何搜索条件**，就允许全量列出结果（超出 15 个气泡则分页）。
            if (string.IsNullOrEmpty(query))
            {
                int total = Search(player, string.Empty).Count;
                Reply(room, player, null, new List<string>
                {
                    $"【我的线索】共 {total} 条（已省略明细，避免刷屏）",
                    "请加条件检索：/clue r:广播室 ｜ /clue p:某人 ｜ /clue a:点燃",
                    "时间段 t:7:00am-7:30am ｜ 排除 -词 ｜ 语法 /clue help",
                });
                return;
            }

            List<ClueRow> rows = Search(player, query);

            var lines = new List<string>();
            lines.Add(rows.Count == 0
                ? "【搜索结果】命中 0 条"
                : $"【搜索结果】命中 {rows.Count} 条");

            for (int i = 0; i < rows.Count; i++)
            {
                lines.Add(rows[i].Text);
            }

            if (rows.Count == 0)
            {
                lines.Add("（没有符合条件的线索；/clue help 看语法）");
            }

            // 打包成气泡，再按「每页最多 15 个气泡」分页。
            List<List<string>> bubbles = PackBubbles(lines, CurrentWidth());
            int pages = (bubbles.Count + MaxBubblesPerPage - 1) / MaxBubblesPerPage;
            if (pages < 1)
            {
                pages = 1;
            }

            if (page > pages)
            {
                page = pages;
            }

            int startIndex = (page - 1) * MaxBubblesPerPage;
            var pageBubbles = new List<List<string>>();
            for (int i = startIndex; i < bubbles.Count && i < startIndex + MaxBubblesPerPage; i++)
            {
                pageBubbles.Add(bubbles[i]);
            }

            SendBubbles(player, player.PublicInfo.PlayerId, pageBubbles);

            if (pages > 1)
            {
                string tip = page < pages
                    ? $"第 {page}/{pages} 页 ｜ 下一页：/clue {query} page:{page + 1}"
                    : $"第 {page}/{pages} 页 ｜ 已到最后一页";
                SendChat(room, player, player, tip);
            }
        }

        // ══════════════════ /who ══════════════════

        /// <summary>
        /// 列出玩家的 <c>PlayerId</c> 与昵称，方便 <c>/say</c> 指定私聊对象。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>为什么需要这个命令</b>：会议里玩家看到的"座位"是 <c>ESeat</c> 角色位
        /// （0.1.18 :17905，Luna/Miyuki/Rin/Hasung/Noel/Seol），与 <c>PlayerId</c> **没有映射**；
        /// 而 <c>PlayerId</c> 只是进房时分配的**网络槽位号**（<c>GetEmptyID</c> :163818 ——
        /// 16 个槽位里取最小的空闲位、返回位次 + 1，玩家离开即释放）。
        /// 所以界面上根本看不到这个编号，必须给一张对照表。
        /// </para>
        /// <para>任何阶段都可用（大厅里也能先认人再私聊）。</para>
        /// </remarks>
        private static void CmdWho(ServerGameRoom room, ServerGamePlayer player)
        {
            if (room.Players == null || room.Players.Count == 0)
            {
                Reply(room, player, "当前没有玩家。");
                return;
            }

            var list = new List<ServerGamePlayer>();
            for (int i = 0; i < room.Players.Count; i++)
            {
                ServerGamePlayer p = room.Players[i];
                if (p != null && p.PublicInfo != null)
                {
                    list.Add(p);
                }
            }

            list.Sort((a, b) => a.PublicInfo.PlayerId.CompareTo(b.PublicInfo.PlayerId));

            int me = player.PublicInfo != null ? player.PublicInfo.PlayerId : -1;
            var lines = new List<string> { "【玩家一览】/say 可用 ID 或昵称" };
            for (int i = 0; i < list.Count; i++)
            {
                ServerGamePlayer p = list[i];
                int id = p.PublicInfo.PlayerId;
                string mark = id == me ? "（你）" : (p.IsAlive ? string.Empty : "（已死亡）");
                lines.Add($"#{id} {p.Name}{mark}");
            }

            Reply(room, player, null, lines);
        }

        // ══════════════════ /help ══════════════════

        /// <summary>报到式帮助：只发本模块的命令，**不吞**（让其它模块继续报各自的）。</summary>
        private static void CmdHelp(ServerGameRoom room, ServerGamePlayer player)
        {
            var lines = new List<string> { "【ClueDetail 命令】" };
            lines.AddRange(AvailableCommands(room));
            Reply(room, player, null, lines);
        }

        // ══════════════════ 发送 ══════════════════

        /// <summary>给某个玩家发一条（或数条，按 3 行切分）聊天消息。</summary>
        /// <param name="speaker">显示成谁说的话；null = 玩家自己（回执）。</param>
        /// <param name="lines">若干行文本；每 <see cref="MaxLinesPerMessage"/> 行打包成一条消息。</param>
        private static void Reply(ServerGameRoom room, ServerGamePlayer player, string single, List<string> lines = null)
        {
            if (lines == null)
            {
                lines = new List<string> { single ?? string.Empty };
            }

            SendLines(player, player, lines);
        }

        /// <summary>把若干行按 3 行一条拆开发给指定玩家。</summary>
        private static void SendLines(ServerGamePlayer to, ServerGamePlayer speaker, List<string> lines)
        {
            if (to == null || to.Session == null || lines == null || lines.Count == 0)
            {
                return;
            }

            int speakerId = speaker != null && speaker.PublicInfo != null
                ? speaker.PublicInfo.PlayerId
                : 0;

            SendBubbles(to, speakerId, PackBubbles(lines, CurrentWidth()), !to.IsAlive);
        }

        /// <summary>当前的折行宽度（配置热读）。</summary>
        private static int CurrentWidth()
        {
            return MaxLineWidth != null ? MaxLineWidth.Value : 50;
        }

        /// <summary>
        /// 把「逻辑行」打包成「消息气泡」，每个气泡 ≤ <see cref="MaxLinesPerMessage"/> 行。
        /// </summary>
        /// <remarks>
        /// <para>
        /// **一条线索允许被拆到多个气泡**（用户口径 2026-10-19 修订：整条挪走会浪费气泡容量），
        /// 但断开处必须**主动截断并加 <c>…</c>** —— 否则玩家会把它当成两条不同的线索。
        /// </para>
        /// <para>
        /// 截断是**真的裁掉行尾 1~2 个字符**以容纳 <c>…</c>（用户口径："需要主动截断文本"）——
        /// 那些字符**不会**接回下一个气泡，代价是丢掉极少量字数。为此把裁切量压到最小：
        /// 先试"原样 + …"，放得下就一个字都不删。
        /// </para>
        /// <para>
        /// ⚠️ 只在**同一条线索**被拆开时打标记；气泡恰好在两条线索之间结束时不打（那不是续行）。
        /// </para>
        /// </remarks>
        private static List<List<string>> PackBubbles(List<string> lines, int width)
        {
            var bubbles = new List<List<string>>();
            var current = new List<string>();

            for (int i = 0; i < lines.Count; i++)
            {
                List<string> block = WrapLine(lines[i], width);
                for (int k = 0; k < block.Count; k++)
                {
                    if (current.Count >= MaxLinesPerMessage)
                    {
                        bubbles.Add(current);
                        current = new List<string>();
                    }

                    current.Add(block[k]);

                    if (current.Count >= MaxLinesPerMessage)
                    {
                        // 本条线索还有后续（k+1 < block.Count）⇒ 这是"续行断点"，打省略号。
                        if (k + 1 < block.Count)
                        {
                            current[current.Count - 1] =
                                TruncateWithEllipsis(current[current.Count - 1], width);
                        }

                        bubbles.Add(current);
                        current = new List<string>();
                    }
                }
            }

            if (current.Count > 0)
            {
                bubbles.Add(current);
            }

            return bubbles;
        }

        /// <summary>在不超过 <paramref name="width"/> 显示宽度的前提下给行尾加 <c>…</c>。</summary>
        private static string TruncateWithEllipsis(string text, int width)
        {
            const string ellipsis = "…";
            if (string.IsNullOrEmpty(text))
            {
                return ellipsis;
            }

            if (WidthOfString(text + ellipsis) <= width)
            {
                return text + ellipsis;
            }

            var sb = new global::System.Text.StringBuilder(text);
            while (sb.Length > 0 && WidthOfString(sb.ToString() + ellipsis) > width)
            {
                sb.Length--;
            }

            return sb.ToString().TrimEnd() + ellipsis;
        }

        /// <summary>把打包好的气泡依次发给指定玩家。</summary>
        /// <param name="isDead">是否按「死者消息」渲染（死者的回执 / 死者发出的私聊）。</param>
        private static void SendBubbles(
            ServerGamePlayer to, int speakerId, List<List<string>> bubbles, bool isDead = false)
        {
            if (to == null || to.Session == null || bubbles == null)
            {
                return;
            }

            for (int i = 0; i < bubbles.Count; i++)
            {
                string text = string.Join("\n", bubbles[i].ToArray());
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                try
                {
                    to.Session.Send(new S_CHAT_MESSAGE
                    {
                        Type = EChatType.NormalChat,
                        Text = text,
                        PlayerId = speakerId,
                        IsDead = isDead,
                    });
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Warn($"游戏内命令：回执发送失败（{ex.Message}）。");
                    return;
                }
            }
        }

        /// <summary>一条私聊/回执（单行）。</summary>
        /// <param name="isDead">是否按「死者消息」渲染（死者发出的私聊必须带上）。</param>
        private static void SendChat(
            ServerGameRoom room, ServerGamePlayer to, ServerGamePlayer speaker, string text, bool isDead = false)
        {
            if (to == null || to.Session == null)
            {
                return;
            }

            try
            {
                to.Session.Send(new S_CHAT_MESSAGE
                {
                    Type = EChatType.NormalChat,
                    Text = text,
                    PlayerId = speaker != null && speaker.PublicInfo != null ? speaker.PublicInfo.PlayerId : 0,
                    IsDead = isDead,
                });
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Warn($"游戏内命令：私聊发送失败（{ex.Message}）。");
            }
        }

        // ══════════════════ 折行 ══════════════════

        /// <summary>
        /// 按**显示宽度**折行：中文/全角算 2，ASCII 算 1。
        /// </summary>
        /// <remarks>
        /// 优先在分隔符（<c>·</c>、空格、逗号）处断开；找不到合适的断点就硬切。
        /// 空行原样保留（用于排版留白）。
        /// </remarks>
        private static List<string> WrapLine(string text, int maxWidth)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text) || maxWidth <= 0)
            {
                result.Add(text ?? string.Empty);
                return result;
            }

            var current = new global::System.Text.StringBuilder();
            int width = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                int w = DisplayWidth(ch);
                if (width + w > maxWidth && current.Length > 0)
                {
                    int cut = LastBreak(current);
                    if (cut > 0 && cut < current.Length)
                    {
                        result.Add(current.ToString().Substring(0, cut).TrimEnd());
                        string rest = current.ToString().Substring(cut).TrimStart();
                        current.Clear();
                        current.Append(rest);
                        width = WidthOfString(rest);
                    }
                    else
                    {
                        result.Add(current.ToString().TrimEnd());
                        current.Clear();
                        width = 0;
                    }
                }

                current.Append(ch);
                width += w;
            }

            if (current.Length > 0)
            {
                result.Add(current.ToString().TrimEnd());
            }

            if (result.Count == 0)
            {
                result.Add(string.Empty);
            }

            return result;
        }

        /// <summary>在已累积的文本里找最后一个可断处（返回断点后的下标）。</summary>
        private static int LastBreak(global::System.Text.StringBuilder sb)
        {
            for (int i = sb.Length - 1; i > 0; i--)
            {
                char c = sb[i];
                if (c == ' ' || c == '·' || c == '，' || c == '、' || c == '/' || c == '：')
                {
                    return i + 1;
                }
            }

            return -1;
        }

        private static int WidthOfString(string s)
        {
            int w = 0;
            for (int i = 0; i < s.Length; i++)
            {
                w += DisplayWidth(s[i]);
            }

            return w;
        }

        /// <summary>单字符显示宽度：CJK / 全角 = 2，其余 = 1。</summary>
        private static int DisplayWidth(char c)
        {
            if (c < 0x1100)
            {
                return 1;
            }

            bool wide = (c >= 0x1100 && c <= 0x115F)
                        || (c >= 0x2E80 && c <= 0xA4CF)
                        || (c >= 0xAC00 && c <= 0xD7A3)
                        || (c >= 0xF900 && c <= 0xFAFF)
                        || (c >= 0xFE30 && c <= 0xFE6F)
                        || (c >= 0xFF00 && c <= 0xFF60)
                        || (c >= 0xFFE0 && c <= 0xFFE6);
            return wide ? 2 : 1;
        }

        // ══════════════════ 本地化 ══════════════════

        /// <summary>房间显示名（与客户端线索行同一套：RoomDic → ERoomType 名 → GetText）。</summary>
        private static string RoomNameOf(int roomId)
        {
            try
            {
                if (Managers.Data.RoomDic.TryGetValue(roomId, out var data))
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

            return $"房间{roomId}";
        }

        /// <summary>活动显示名（与客户端同一套：<c>GetText(what.ToString())</c>）。</summary>
        private static string WhatNameOf(int what)
        {
            string key = ((EWhatType)what).ToString();
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

        /// <summary>游戏内时钟（与 <c>Util.FormatTimeSentence</c> 同口径：1 小时 = 60 SurviveTime 秒）。</summary>
        private static string ClockOf(int seconds)
        {
            int hour = seconds / 60;
            int minute = seconds % 60;
            string meridiem = hour < 12 ? "am" : "pm";
            if (hour > 12)
            {
                hour -= 12;
            }

            return $"{hour:00}:{minute:00} {meridiem}";
        }
    }
}