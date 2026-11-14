using System;
using Protocol;

namespace ClueDetail.Core
{
    /// <summary>
    /// 「详细活动」的唯一解析入口。
    /// </summary>
    /// <remarks>
    /// 三级来源，可靠性递减：
    /// <list type="number">
    /// <item><b>服务端精确下发</b> —— 房主装了本模块时走这条（<see cref="UnknownFields"/>，字段号 100）。</item>
    /// <item><b>设备关联</b> —— 两条路都精确到设备：① <c>S_ADD_CLUE</c> 自带 <c>DeviceId</c>
    /// （经 <c>Server.Game.ClueMirror.Add</c> 记下）；② <c>S_SCAN_DEVICE</c> 不带设备号，
    /// 但那是本机刚发起的扫描，靠请求/响应配对得到。</item>
    /// <item><b>按房间推断</b> —— 房主没装时的主力：用线索的房间去设备表里找该房间有什么活动。
    /// 去重后只剩一种 → 确定；两种以上 → <c>ambiguous</c>，调用方退化为通用文案。</item>
    /// </list>
    /// </remarks>
    internal static class ClueWhat
    {
        public static EWhatType Resolve(ClueManager.Clue clue)
        {
            return TryResolve(clue, out EWhatType what, out _) ? what : EWhatType.WNone;
        }

        /// <summary>
        /// **只给卡片图像用**的解析：在 <see cref="Resolve"/> 之外，额外把「黑幕切断了电力」映射成电闸。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 为什么要单独一个入口：11037（<c>Define.MASTERMIND_SABOTAGE_ID</c>）在 <see cref="TryResolve"/>
        /// 里是**故意拒绝**的（⓪.1 守卫）—— 它不是一个"设备活动"，原版句式已经把它渲染成
        /// 「切断了电力」，若让文本走活动替换，会去贴该房间某台设备平时的活动（那是错的）。
        /// </para>
        /// <para>
        /// 但**图像**那一格不该留空：旧版这里显示的就是"破坏电闸"这台设备的卡面，
        /// 而 0.1.18 的资源里 <c>WFusebox_card</c> 确实存在（没有房间变体，只有一张）。
        /// 所以卡面单独走这一条：文本保持原版，图用电闸。
        /// </para>
        /// </remarks>
        public static EWhatType ResolveForCard(ClueManager.Clue clue)
        {
            EWhatType what = Resolve(clue);
            if (what != EWhatType.WNone)
            {
                return what;
            }

            if (clue == null || clue.Info == null)
            {
                return EWhatType.WNone;
            }

            // 「销毁了证据」（11038 主谋 / 66613 黑，Define.IsDestroyEvidenceClue :95774）：
            // **文本保持原版**（TryResolve ⓪ 已拒绝），但卡面必须是**被销毁的那台设备**。
            // 设备号来自 S_ADD_CLUE 自带的 DeviceId —— ClueRender 的 Prefix 已把它记进
            // ActivityMap（键 = 线索的 PlayerId/Time/RoomId，角色线索同样适用）。
            if (Define.IsDestroyEvidenceClue(clue.Info))
            {
                EWhatType byDevice = ActivityMap.WhatOfDevice(
                    ActivityMap.DeviceOf(clue.Info.PlayerId, clue.Info.Time, clue.Info.RoomId));
                if (byDevice != EWhatType.WNone)
                {
                    return byDevice;
                }

                // 兜底：房主本机不走镜像（客户端 Handle_S_ADD_CLUE 直接跳过房主），
                // 或扫描配对失败时，房间只剩一种活动才敢用。
                var candidates = RoomDeviceScan.CandidatesOf(clue.Info.RoomId);
                if (candidates.Count == 1)
                {
                    return candidates[0];
                }

                return EWhatType.WNone;
            }

            if (clue.Info.PlayerId == Define.MASTERMIND_SABOTAGE_ID)
            {
                return EWhatType.WFusebox;
            }

            return EWhatType.WNone;
        }

        /// <summary>
        /// 解析详细活动。<paramref name="ambiguous"/> 为 true 表示"知道房间里有活动，但不止一种"，
        /// 此时 <paramref name="what"/> 无意义。
        /// </summary>
        /// <remarks>
        /// 四级来源，可靠性递减；每一级的命中都经 <see cref="Diagnostics.TraceClue"/> 记一行
        /// （实机出问题时，"到底断在哪一环"靠这行日志判断，而不是靠猜）。
        /// </remarks>
        public static bool TryResolve(ClueManager.Clue clue, out EWhatType what, out bool ambiguous)
        {
            what = EWhatType.WNone;
            ambiguous = false;

            if (clue == null || clue.Info == null)
            {
                return false;
            }

            int roomId = clue.Info.RoomId;
            int playerId = clue.Info.PlayerId;
            int time = clue.Info.Time;

            // 房主装了但关掉了本模块 → 连客户端自采集也一起停，否则等于把房主关掉的信息又补回来。
            if (!RoomState.AllowDetail)
            {
                Diagnostics.TraceClue(roomId, playerId, time, 0, false, "本局禁用");
                return false;
            }

            // ⓪ 「销毁证据」线索**必须保持原版**（2026-10-18 实机反馈「黑那行显示成点燃了蜡烛」查出）。
            // 判据用游戏自己的口径：Define.IsDestroyEvidenceClue(:95774) = PlayerId ∈ {11038, 66613}
            // （11038 = MASTERMIND_DESTROY_ID、66613 = 黑），它们由 Device.DestroyEvidence（:166482）造出来，
            // 描述的是"这台设备的证据被销毁了"这个**动作**，不是该设备平时的活动。
            // 原版对它们渲染的就是 GetRoleWhatKey(11038/66613) → WhatDestroyEvidence =「销毁了证据」，
            // 而设备关联会把该设备**平时的活动**（教室点蜡烛 → 「点燃了蜡烛」）贴上去 —— 那是错的。
            if (Define.IsDestroyEvidenceClue(clue.Info))
            {
                Diagnostics.TraceClue(roomId, playerId, time, 0, false, "销毁证据线索（保持原版）");
                return false;
            }
            // ⓪.1 **角色级**的另一半：黑幕「切断了电力」（11037）。
            // 既来自原版撤电闸 —— Device.RecordLastUsingPlayer 的 EWhatState.Mastermind
            // 把它写成 `playerId = 11037`（0.1.18 :166397），也来自我们的隐蔽破坏点
            // （ShadowSabotage/Logic.cs:1216）。两者形状完全一致：PlayerId=11037、
            // **不带 WhatType、不带未知字段**（原版一直如此：0.1.17a/0.1.18 都用默认 WNone）。
            // 0.1.18 对这一段渲染 GetRoleWhatKey(11037) → Define.WHAT_CUT_POWER =「切断了电力」
            // （:27517 / :27379 / 常量 :94591）；若不在最前面挡掉，②/③/④ 会把**该房间某台设备
            // 平时的活动**（例如「翻找了书架」）贴到"切断了电力"上 —— 那既不是旧版行为
            // （旧版这里读的是 clue.Info.WhatType，本就是 WNone），也不是现版语义。
            // 判据直接用游戏自己的 Define.IsRoleClueId（0.1.18 :95765）= {11037, 11038, 66613}：
// ⓪ 已单独接住后两者，这里实际接住 11037；用谓词而不是字面量，将来加 id 也不会漏。
            // ⚠️ 2026-10-19 由线索文本管线评估查出（F1）：此前只有 ⓪，11037 会掉到 ③/④。
            if (Define.IsRoleClueId(playerId))
            {
                Diagnostics.TraceClue(roomId, playerId, time, 0, false, "黑幕切断电力线索（保持原版）");
                return false;
            }

            // ① 服务端精确下发（房主装了本模块时走这条）
            int fromPacket = UnknownFields.GetWhat(clue.Info);
            if (fromPacket != 0)
            {
                what = (EWhatType)fromPacket;
                Diagnostics.TraceClue(roomId, playerId, time, fromPacket, false, "包内未知字段");
                return true;
            }

            // ①.5 房主本机：服务端旁路表。
            // 为什么要这一条：房主自己的客户端**不走镜像** —— 客户端
            // PacketHandler.Handle_S_ADD_CLUE（0.1.18 :43621）第一行就是
            // `if (Managers.Host == null || !Managers.Host.IsHost)`，房主读的是服务端 ClueList 里的对象。
            // 那些对象现在由 RecordDetail 的 Postfix 补写（远端客户端仍走 ① 的包内字段），
            // 这里再兜一层：万一界面拿的是早于补写的副本，也能按同一把键捞回来。
            if (RoomState.IsHost)
            {
                int fromHost = DetailStore.Recall(playerId, time, roomId);
                if (fromHost != 0)
                {
                    what = (EWhatType)fromHost;
                    Diagnostics.TraceClue(roomId, playerId, time, fromHost, false, "房主旁路表");
                    return true;
                }
            }

            // ② 广播自带的设备号（客户端本来就知道这条线索属于哪台设备）
            EWhatType byDevice = ActivityMap.WhatOfDevice(
                ActivityMap.DeviceOf(playerId, time, roomId));
            if (byDevice != EWhatType.WNone)
            {
                what = byDevice;
                Diagnostics.TraceClue(roomId, playerId, time, (int)byDevice, false, "设备关联");
                return true;
            }

            // ③ / ④ 客户端自采集
            if (!SelfCapture.On)
            {
                Diagnostics.TraceClue(roomId, playerId, time, 0, false, "自采集已关");
                return false;
            }

            var candidates = RoomDeviceScan.CandidatesOf(roomId);
            if (candidates.Count == 0)
            {
                Diagnostics.TraceClue(roomId, playerId, time, 0, false, "房间无设备（三个来源都没命中）");
                return false;
            }

            if (candidates.Count == 1)
            {
                what = candidates[0];
                Diagnostics.TraceClue(roomId, playerId, time, (int)what, false, "房间唯一设备");
                return true;
            }

            ambiguous = true;
            Diagnostics.TraceClue(roomId, playerId, time, 0, true, $"房间有 {candidates.Count} 种设备");
            return true;
        }

        /// <summary>
        /// 取词条文本。0.1.18 的语言表删掉了这些键，所以先确保已注入，再判断是否命中
        /// （命中不到时 <c>Managers.GetText</c> 会返回 key 本身，这里据它判失败）。
        /// </summary>
        public static string TextOf(EWhatType what)
        {
            if (what == EWhatType.WNone)
            {
                return null;
            }

            Localization.Ensure();

            string key = what.ToString();
            string text = Managers.GetText(key);
            if (string.IsNullOrEmpty(text) || text == key)
            {
                return null;
            }

            return text;
        }

        /// <summary>
        /// 把真实活动拼进句子里（= 还原 0.1.17a 的行为）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// ⚠️ <b>2026-10-19 定案（前两次结论都错了，这次是拿 0.1.18 运行时真实文案核的）</b>：
        /// 句子模板是**两族**，取自 <c>Managers.GetText("PropositionSentence" + 角色名)</c>
        /// 或 <c>"PropositionSentenceRole" + 角色名</c>（<c>Util.GetPropositionSentence</c> :27510，
        /// 拼接时后缀在前、角色名在后）。两族在 0.1.18 的文案**不一样**：
        /// </para>
        /// <list type="bullet">
        /// <item><c>PropositionSentence{角色}</c>（**普通线索**，<c>IsRoleClueId == false</c>）：
        /// 「…在{when}\n**位于{where}**。」—— <b>模板里根本没有 <c>{what}</c></b>；</item>
        /// <item><c>PropositionSentenceRole{角色}</c>（**角色线索** 11037/11038/66613）：
        /// 「…在{when}\n**于{where}{what}**。」—— 仍带 <c>{what}</c>。</item>
        /// </list>
        /// <para>
        /// 所以 <c>Util.FormatProposition</c>（:27343）的 <c>case "what"</c> 分支**只在角色线索上**会执行；
        /// 普通线索的 <c>{what}</c> 是在**文案层**被删掉的 —— 光看代码会误以为没删
        ///（这也是本模块 10-18 记成"模板没删 {what}"的原因：那份数据表是旧版残留）。
        /// </para>
        /// <para>
        /// 本次处理：
        /// ① 句子里有 <c>&lt;#FF97AB&gt;…&lt;/color&gt;</c>（角色线索）⇒ **替换**那一段；
        /// ② 否则命中「位于{where}」（普通线索）⇒ 把「位于」还原成旧版的「于」，
        /// 并在地点后**插入**活动 —— 等价于 0.1.17a 的「于{where}{what}。」句式。
        /// </para>
        /// </remarks>
        public static bool TryDecorate(string sentence, ClueManager.Clue clue, out string result)
        {
            result = sentence;
            if (string.IsNullOrEmpty(sentence))
            {
                return false;
            }

            if (!TryResolve(clue, out EWhatType what, out bool ambiguous))
            {
                return false;
            }

            string text = ambiguous ? Localization.GenericActivity : TextOf(what);
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            System.Text.RegularExpressions.Match match = WhatSegment.Match(sentence);
            if (match.Success)
            {
                // ① 角色线索：模板里有 {what}，替换那段颜色标记即可。
                result = sentence.Substring(0, match.Index)
                         + "<#FF97AB>" + text + "</color>"
                         + sentence.Substring(match.Index + match.Length);
                return true;
            }

            // ② 普通线索：0.1.18 的文案只到「位于{where}。」，没有活动占位符。
            //    把「位于」还原成旧版的「于」，并在地点后面插入活动（= 0.1.17a 的「于{where}{what}。」）。
            System.Text.RegularExpressions.Match located = LocatedSegment.Match(sentence);
            if (!located.Success)
            {
                return false;
            }

            result = sentence.Substring(0, located.Index)
                     + "于" + located.Groups["where"].Value
                     + "<#FF97AB>" + text + "</color>"
                     + sentence.Substring(located.Index + located.Length);
            return true;
        }

        /// <summary>
        /// 0.1.18 普通线索模板里的地点段：「位于」+ 渲染后的房间名（带 <c>&lt;#FFBE61&gt;</c> 色号）。
        /// </summary>
        /// <remarks>
        /// 用「位于」当锚点是安全的：<c>{who}</c> / <c>{when}</c> 也渲染成 <c>&lt;#FFBE61&gt;</c>，
        /// 但只有地点前面会出现「位于」二字（旧版是「于」）。
        /// </remarks>
        private static readonly System.Text.RegularExpressions.Regex LocatedSegment =
            new System.Text.RegularExpressions.Regex("位于(?<where><#FFBE61>[^<]*</color>)");

        /// <summary>
        /// <c>{what}</c> 在渲染结果里的样子（0.1.18 的固定色号）。
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex WhatSegment =
            new System.Text.RegularExpressions.Regex("<#FF97AB>[^<]*</color>");
    }
}
