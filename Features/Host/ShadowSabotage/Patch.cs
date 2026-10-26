using ClueDetail.Core;
using Google.Protobuf;
using HarmonyLib;
using Protocol;
using ServerDevice = Server.Game.Device;
using ItemManager = Server.Game.ItemManager;
using ServerDeviceManager = Server.Game.DeviceManager;
using ServerGamePlayer = Server.Game.Player;
using ServerGameRoom = Server.Game.GameRoom;
using ServerGameHostPeerSession = Server.Game.HostPeerSession;
using ServerTimeManager = Server.Game.TimeManager;
using ServerMirror = Server.Game.Mirror;
#if TIMELINE
using ServerTimelineRecorder = Server.Game.TimelineRecorder;
#endif   // TIMELINE

namespace ClueDetail.Features.Host.ShadowSabotage
{
    /// <summary>
    /// 隐蔽破坏点的全部 Harmony 挂点。补丁类必须是 Feature 的**直接**嵌套类型（只扫一层）。
    /// </summary>
    internal partial class ShadowSabotageFeature
    {
        /// <summary>
        /// 「新一轮可破坏窗口开启」= 原版电闸 CD 到点的信号。
        /// </summary>
        /// <remarks>
        /// <c>DeviceManager.StartFuseboxSabotage</c>（0.1.18 :166828）只有两个调用点，
        /// 语义都对得上：<c>InitDevices</c>（开局，:167336）与
        /// <c>AreaManager.RefreshLight</c> 的 case 0（供电全恢复 + 60 秒后，:176585）。
        /// 见 <see cref="OnWindowStart"/>。
        /// </remarks>
        [HarmonyPatch(typeof(ServerDeviceManager), nameof(ServerDeviceManager.StartFuseboxSabotage))]
        internal static class WindowHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!FeatureLoader.Enabled<ShadowSabotageFeature>()) return;
                try
                {
                    OnWindowStart();
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "破坏点：窗口开启处理失败。");
                }
            }
        }

        /// <summary>
        /// 每秒一次的维护（设备保活 + 假主人名单）。
        /// </summary>
        /// <remarks>
        /// 用字符串定位：<c>GameRoom.SurvivalTick</c> 不是 public（与 HideAndSeek 的挂法一致）。
        /// </remarks>
        [HarmonyPatch(typeof(ServerGameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!FeatureLoader.Enabled<ShadowSabotageFeature>()) return;
                try
                {
                    Tick();
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "破坏点：每秒维护失败。");
                }
            }
        }

        /// <summary>
        /// 拖着进度条时 **吃掉丢弃请求**。
        /// </summary>
        /// <remarks>
        /// 客户端 <c>DropHand</c> 是**先本地清手、再发 <c>C_DROP_ITEM</c>**，
        /// 而我们的汽水只是他屏幕上的幻觉、服务端 <c>Player.Hand</c> 全程没变
        /// ⇒ 放行的话他会把**真物品**丢在地上，同时自己屏幕上的进度条消失。
        /// 这里吃掉包并重申幻觉汽水，进度看起来是连续的。
        /// </remarks>
        [HarmonyPatch(typeof(ItemManager), nameof(ItemManager.DropItem))]
        internal static class DropItemHook
        {
            [HarmonyPrefix]
            private static bool Prefix(ServerGamePlayer player)
            {
                if (!FeatureLoader.Enabled<ShadowSabotageFeature>()) return true;
                if (player == null || player.PublicInfo == null)
                {
                    return true;
                }

                int pid = player.PublicInfo.PlayerId;
                if (!BreakBar.IsActive(pid))
                {
                    return true;
                }

                BreakBar.Resend(pid);
                return false;
            }
        }

        /// <summary>
        /// 拖着进度条时 **吃掉拾取请求**。
        /// </summary>
        /// <remarks>
        /// 服务端拾取成功会给他发 <c>S_ADD_ITEM</c> ⇒ 客户端 <c>InsertHand</c> 把幻觉汽水顶掉、
        /// 进度条消失。5 秒内不能捡东西是可接受的代价。
        /// </remarks>
        [HarmonyPatch(typeof(ItemManager), nameof(ItemManager.AcquireItem))]
        internal static class AcquireItemHook
        {
            [HarmonyPrefix]
            private static bool Prefix(ServerGamePlayer player)
            {
                if (!FeatureLoader.Enabled<ShadowSabotageFeature>()) return true;
                if (player == null || player.PublicInfo == null)
                {
                    return true;
                }

                int pid = player.PublicInfo.PlayerId;
                if (!BreakBar.IsActive(pid))
                {
                    return true;
                }

                BreakBar.Resend(pid);
                return false;
            }
        }

        /// <summary>
        /// 离开 Survive 阶段：收尾进度条与假主人。**不**重置破坏状态
        /// —— 设备要留到调查阶段（可被扫描），外观也保持被破坏过的样子。
        /// </summary>
        [HarmonyPatch(typeof(ServerGameRoom), nameof(ServerGameRoom.ChangeGameState), new[] { typeof(EGameState) })]
        internal static class LeaveSurviveHook
        {
            [HarmonyPostfix]
            private static void Postfix(EGameState state)
            {
                if (!FeatureLoader.Enabled<ShadowSabotageFeature>()) return;
                if (state == EGameState.Survive)
                {
                    return;
                }

                try
                {
                    for (int i = 0; i < BreakerStates.All.Length; i++)
                    {
                        var st = BreakerStates.All[i];
                        AbortSession(st);
                        AbortFakes(st);

                        // 箭头不随阶段自动清，离开 Survive 时自己撤掉（否则会留在画面上）。
                        if (st.ArrowSent)
                        {
                            BroadcastRemoveArrow(FindDevice(st));
                            st.ArrowSent = false;
                        }
                    }

                    // 镜子的熄灭**不在这里** —— 见 DetectiveStartHook：
                    // 本钩子在 StartDetective() 之前执行，而后者内部的 DeviceManager.StartDetective()
                    // 会把真值 StateList[1]/[2] 重新广播一遍，在这里熄灭等于白断。
                    // （原调用 QuenchImpossibleMirrors() 已挪到 DetectiveStartHook。）
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "破坏点：离开 Survive 收尾失败。");
                }
            }
        }

        /// <summary>
        /// 进入调查阶段：把仍残留污染记录的镜子熄灭。
        /// </summary>
        /// <remarks>
        /// <para>
        /// ⚠️ 2026-10-20 二次改：必须挂在 <c>StartDetective</c> 的 **Postfix**，不能挂在
        /// <c>ChangeGameState</c> 上。后者只是**发起**切换（<c>State</c> 立刻改、<c>IsTransitioning = true</c>），
        /// 真正的 <c>StartDetective()</c> 在 <c>BroadcastPacketAndWaitResponse</c> 的回调里（0.1.18 :173063）；
        /// 而 <c>StartDetective()</c> 内部会调 <c>DeviceManager.Instance.StartDetective()</c>（:174544）
        /// ⇒ 每台设备 <c>StartDetective()</c> → <c>BroadcastState()</c>，把真值 <c>StateList[1]/[2]</c>
        /// **重新推到客户端** —— 在 ChangeGameState 上熄灭随后就被这次广播盖回去了
        /// （实机：熄灭日志打了 4 次，画面上仍是真人真时间）。
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(ServerGameRoom), "StartDetective")]
        internal static class DetectiveStartHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!FeatureLoader.Enabled<ShadowSabotageFeature>()) return;

                try
                {
                    QuenchImpossibleMirrors();

                    // 兜底：时钟一直没修好、直接进调查阶段时，把攒下的回放帧补发出去。
#if TIMELINE
                    TimelineReplayDelayHook.Flush();
#endif   // TIMELINE
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "破坏点：调查阶段熄灭镜子失败。");
                }
            }
        }

        // ══════════════════ P2：线索污染 ══════════════════

        /// <summary>
        /// 身份污染：身份识别组件被破坏期间，新写入的线索一律记成「某人」。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 做法就是把 <c>state</c> 从 <c>Normal</c> 改成 <c>Anonymity</c>；
        /// 原版 <c>RecordLastUsingPlayer</c>（0.1.18 :166393）对 <c>Anonymity</c> 的处理
        /// 恰好就是 <c>playerId = 0</c>，客户端渲染成词条 <c>WhoSomeone</c>（中文「某人」）。
        /// </para>
        /// <para>
        /// <b>只改 <c>Normal</c></b> 是刻意的：<c>Mastermind</c> / <c>Black</c> 那三类角色线索
        /// （11037 / 66613 / 11038）本来就是「关于黑幕 / 黑方自己」的，不受污染影响 ——
        /// 这正是需求里「除去进行这个操作的人所自动获取的关于自己的线索」。
        /// </para>
        /// <para>
        /// ⚠ <c>[HarmonyPriority(Priority.First)]</c> 不能省：本模块的
        /// <c>RecordDetail.RecordHook</c> 也挂这个方法的 Prefix，它按 <c>state</c> 算出
        /// <c>playerId</c> 记进旁路表。我们在它**之前**改掉 <c>state</c>，
        /// 两边算出的 <c>playerId</c> 才会一致（都是 0），旁路表的键才对得上。
        /// </para>
        /// </remarks>
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(ServerDevice), nameof(ServerDevice.RecordLastUsingPlayer))]
        internal static class IdentityPolluteHook
        {
            [HarmonyPrefix]
            private static void Prefix(ref Define.EWhatState state)
            {
                if (!FeatureLoader.Enabled<ShadowSabotageFeature>()) return;
                if (state != Define.EWhatState.Normal)
                {
                    return;                 // 角色线索豁免
                }

                if (!IdentifyPollutionOn)
                {
                    return;
                }

                // 按配置选占位符（2026-10-19 用户定案：三选一）。
                // 原版 RecordLastUsingPlayer 对这三个 state 的处理正好给出三个 id：
                //   Anonymity ⇒ 0、Black ⇒ 66613、Mastermind ⇒ 11037（0.1.18 :166393-166401）。
                // ⚠️ 选 Black/Mastermind 时原版还会给提出者发 S_LEFT_CLUE{ RoleFace = true }，
                //    客户端会播"角色线索"的提示 —— 那是原版语义，接受。
                //
                // ⚠️ 2026-10-20 改（用户口径）：「某人」**不改真值**。
                //    预期行为是「每个人记录的**自己的**线索是真实的，只有别人搜集到的才匿名」。
                //    真值一旦写成 0，代价有两个：
                //      ① 当事人自己的线索被 StartDetective 的 `PlayerId == 自己` 过滤掉
                //         （0.1.18 :174525）⇒ 他自己的初始线索列表里没有这条；
                //      ② 同一台设备、同一个 30 秒桶内，**所有人**的线索都会被原版合并成一条
                //         （因为大家的 PlayerId 都变成了 0，:166405）。
                //    所以「某人」档改为**只靠出站改写**（Wire.cs 的 RewriteOutflow 按收件人匿名）：
                //    真值保留真实 id，未装本模块的客户端照样收到 0。
                //    Black / Mastermind 两档**必须**改真值 —— 那是「嫁祸给角色」，不是匿名。
                switch (PlaceholderValue)
                {
                    case PollutionPlaceholder.Black:
                        state = Define.EWhatState.Black;
                        break;
                    case PollutionPlaceholder.Mastermind:
                        state = Define.EWhatState.Mastermind;
                        break;
                    default:
                        return;                 // 某人：真值保持真实 id
                }
            }
        }

        /// <summary>
        /// 打标：时钟破坏点已破坏期间写进 <c>ClueList</c> 的线索，额外记一个「显示时刻」未知字段（101）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>2026-10-18 夜二次改法</b>（旧的 <c>TimePolluteBroadcastHook</c> / <c>TimePolluteScanHook</c> 已删）。
        /// 原因：界面里的时间**不读** <c>S_ADD_CLUE</c>（它只喂 <c>ClueMirror</c>，全仓只被
        /// <c>HasDestroyableClue</c> 与迁移快照读，界面从不渲染），而是读 `S_SCAN_DEVICE`
        /// （<c>Device.Scan</c> :166318）与 `S_ADD_MY_CLUES`（:174520-174540）。
        /// 旧写法两条都没覆盖到 —— <c>Device.Scan</c> 的整替被 <see cref="TimePollutionOn"/> 的 Survive 门挡着
        /// （扫描只发生在调查阶段），<c>S_ADD_CLUE</c> 又根本没进界面 ⇒ **远端与房主都看不到冻结时间**。
        /// </para>
        /// <para>
        /// 新写法：写入时打标（**真值一个字都不改**，30 秒去重窗口与迁移都安全），
        /// 由 <see cref="DisplayTimeOutflowHook"/> 在出站副本上把 <c>Time</c> 换成它。
        /// </para>
        /// <para>
        /// 为什么按 "Time == 当刻 SurviveTime" 找条目：原版刚写 / 刚更新的一定是这一秒的那条
        /// （合并分支也会把 Time 更新成当刻，:166425）；同一秒写进来的其它线索同样处于污染窗口内，
        /// 一起打标是**正确**的。
        /// </para>
        /// </remarks>
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(ServerDevice), nameof(ServerDevice.RecordLastUsingPlayer))]
        internal static class DisplayTimeStampHook
        {
            [HarmonyPostfix]
            private static void Postfix(ServerDevice __instance, ServerGamePlayer player)
            {
                StampPollution(__instance, player);
            }
        }

        /// <summary>
        /// 给「刚写进 <c>ClueList</c> 的那条线索」打污染标记：字段 <b>101</b>（显示时刻 = 冻结值）
        /// 与字段 <b>103</b>（真实当事人）。
        /// </summary>
        /// <remarks>
        /// 两个标记互相独立：时间污染（时钟记录组件）写 101，身份污染（身份识别组件）写 103。
        /// 之所以按 <c>Time == 当刻 SurviveTime &amp;&amp; RoomId == 本设备房间</c> 找条目：
        /// 原版刚写 / 刚更新的一定是这一秒那条（合并分支也会把 <c>Time</c> 刷成当刻，0.1.18 :166425）。
        /// <para>
        /// ⚠ 两条调用路径：<see cref="DisplayTimeStampHook"/> 的 Postfix（原版方法正常跑时），
        /// 以及 <c>ClueMerge</c> 的自定义窗口接管（那时 Postfix 不会跑）—— 见
        /// <c>Features/Host/ClueMerge/Patch.cs</c>。
        /// </para>
        /// </remarks>
        internal static void StampPollution(ServerDevice __instance, ServerGamePlayer player)
        {
            if (!FeatureLoader.Enabled<ShadowSabotageFeature>()) return;
            if (__instance == null || __instance.ClueList == null) return;

            // ⚠️ 2026-10-20：**不能**因为「当前没污染」就整体返回（旧写法就是那样）。
            // 原版合并分支会命中一条**旧**线索（同一人 + 同一设备 + 同一 30 秒桶内）：
            // 如果那条是破坏期写的、而这次是修复之后写的，就必须把旧标记**清掉** ——
            // 否则修复后记录的线索仍会被匿名 / 冻结（污染状态永久粘在条目上）。
            var tm = ServerTimeManager.Instance;
            if (tm == null)
            {
                return;
            }

            bool timeOn = TimePollutionOn;
            bool identityOn = IdentifyPollutionOn;
            int now = tm.SurviveTime;
            int frozen = FrozenTime;
            int owner = player != null && player.PublicInfo != null ? player.PublicInfo.PlayerId : 0;
            var list = __instance.ClueList;
            for (int i = 0; i < list.Count; i++)
            {
                var info = list[i];
                if (info == null || info.Destroyed || info.Time != now || info.RoomId != __instance.RoomID)
                {
                    continue;
                }

                if (timeOn)
                {
                    UnknownFields.SetDisplayTime(info, frozen);
                }
                else
                {
                    UnknownFields.ClearValue(info, UnknownFields.DisplayTimeFieldNumber);
                }

                if (identityOn)
                {
                    UnknownFields.SetRealOwner(info, owner);
                }
                else
                {
                    UnknownFields.ClearValue(info, UnknownFields.RealOwnerFieldNumber);
                }
            }

            // 「某人」+ 预热：原版刚把这条线索广播成 PlayerId=0，客户端此时拿不到 Someone_SD
            // （0.1.18 未把它注册进 catalog）⇒ 头像会保留该控件上一次的真人图。
            // 这里把这条线索"重演"一遍：先换成「黑」占位让控件刷上 Black_SD，下一帧再换回「某人」。
            if (PreheatValue && IdentifyPollutionOn)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var info = list[i];
                    if (info == null || info.Destroyed || info.Time != now || info.RoomId != __instance.RoomID)
                    {
                        continue;
                    }

                    if (info.PlayerId != 0)
                    {
                        continue;          // 只对「某人」做；Black/Mastermind 占位符本身就有图
                    }

                    StartPreheat(__instance, info);
                    break;
                }
            }
        }

        /// <summary>
        /// 给**指定那一条**线索打污染标记（2026-10-20 新增，确定性版本）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 为什么需要它：<see cref="StampPollution"/> 是**按时间反查**的
        /// （<c>info.Time == tm.SurviveTime</c>），而调用方通常已经在方法开头读过一次
        /// <c>SurviveTime</c> —— 两次读取之间只要跨过整数边界（628.99 → 629.00），
        /// 那条线索就匹配不上 ⇒ 一个标记都不打。实机症状：时钟破坏期间写的线索
        /// 出站时全是真实时间（日志里「线索合并窗口」有、「出站改写线索」没有）。
        /// </para>
        /// <para>
        /// 所以凡是**手里已经拿着对象引用**的调用方（<c>ClueMerge.Execute</c>）都用这个，
        /// 不再靠时间猜；<see cref="StampPollution"/> 保留给拿不到引用的路径（原版 Postfix）。
        /// </para>
        /// </remarks>
        /// <param name="info">要打标的那条线索（真值对象，不是副本）。</param>
        /// <param name="owner">真实当事人 PlayerId（写 103）。</param>
        internal static void StampOne(
            PropositionInfo info, int owner, bool timeOn, bool identityOn, int frozen)
        {
            if (info == null)
            {
                return;
            }

            if (timeOn)
            {
                UnknownFields.SetDisplayTime(info, frozen);
            }
            else
            {
                UnknownFields.ClearValue(info, UnknownFields.DisplayTimeFieldNumber);
            }

            if (identityOn)
            {
                UnknownFields.SetRealOwner(info, owner);
            }
            else
            {
                UnknownFields.ClearValue(info, UnknownFields.RealOwnerFieldNumber);
            }
        }

        /// <summary>
        /// 用一条「黑」的假线索把客户端控件"预热"成黑方头像，再换回真正的「某人」线索。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>原理</b>：客户端 <c>UI_ClueSubItem</c> 是池化复用的，而 <c>PlayerId == 0</c> 时
        /// <c>Someone_SD</c> 加载失败、控件**什么都不写** ⇒ 头像留在上一次的值。
        /// 若让同一条线索（同 <c>Time</c>、同 <c>RoomId</c>）先以「黑」（66613，图可加载）
        /// 被渲染一次，再换回「某人」，列表按 <c>Time</c> 排序 ⇒ 同一位置 ⇒ 复用同一控件
        /// ⇒ 头像保持 <c>Black_SD</c>，不再泄漏真人。
        /// </para>
        /// <para>
        /// ⚠️ <b>删除包必须带 103、占位包必须不带 103</b>：
        /// 出站改写会把带 103 的线索按收件人换成真实 <c>PlayerId</c>（当事人看实名），
        /// 所以"删掉原版那条"要带 103（当事人手上存的是真实 id，别人的是 0），
        /// 而"塞入 66613"必须清掉 103，否则会被改回 0/真实 id，预热就白做了。
        /// </para>
        /// <para>
        /// ⚠️ 只是**尽力而为**：预热只保证紧接着的那一次渲染；列表之后再次滚动/刷新，
        /// 该控件被别的线索占用后，「某人」的头像又会变成那条线索的。列表未打开时预热不生效。
        /// </para>
        /// </remarks>
        private static void StartPreheat(ServerDevice device, PropositionInfo truth)
        {
            var room = ServerGameRoom.Instance;
            if (room == null || device == null || truth == null || truth.PlayerId != 0)
            {
                return;
            }

            try
            {
                // ① 撤掉原版刚发的「某人」（带 103：按收件人换回各自手上的 PlayerId）
                room.Broadcast(new S_REMOVE_CLUE
                {
                    DeviceId = device.ID,
                    Info = truth.Clone(),
                });

                // ② 塞一条「黑」占位（清 103，否则会被改写回真实 id）
                PropositionInfo black = Plain(truth);
                black.PlayerId = 66613;
                room.Broadcast(new S_ADD_CLUE
                {
                    DeviceId = device.ID,
                    Info = black.Clone(),
                });

                // ③ 下一帧：删占位、发回真正的「某人」（带 103，恢复"当事人看实名"）
                room.PushAfter(0, delegate
                {
                    try
                    {
                        room.Broadcast(new S_REMOVE_CLUE
                        {
                            DeviceId = device.ID,
                            Info = black.Clone(),
                        });
                        room.Broadcast(new S_ADD_CLUE
                        {
                            DeviceId = device.ID,
                            Info = truth.Clone(),
                        });
                    }
                    catch (global::System.Exception ex)
                    {
                        global::ClueDetail.Core.Log.Warn($"破坏点：预热复位失败（{ex.Message}）。");
                    }
                });
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Warn($"破坏点：预热失败（{ex.Message}）。");
            }
        }

        /// <summary>清掉 103（真实当事人）的副本 —— 预热包必须按"网线上的样子"发。</summary>
        private static PropositionInfo Plain(PropositionInfo info)
        {
            PropositionInfo copy = info.Clone();
            UnknownFields.SetRealOwner(copy, 0);
            return copy;
        }

        /// <summary>
        /// 出站口统一改写：对 <c>S_SCAN_DEVICE</c> / <c>S_ADD_MY_CLUES</c> 这两条**真正喂界面**的包，
        /// 把带「显示时刻」标记的线索换成冻结值（只在副本上改）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 为什么挂在 <c>HostPeerSession.Send(IMessage)</c>（public，0.1.18 :175626）：
        /// **服务端给每个玩家的下发都要经过它** —— 远端是 <c>SteamP2PSession</c>，
        /// 房主自己是 <c>LocalLoopbackSink</c>（:162611，会重新序列化）⇒ 房主与远端**一视同仁**；
        /// 未装本模块的客户端也一样，因为我们改的是 <c>Time</c> 本身。
        /// </para>
        /// <para>
        /// ⚠ 这两个包塞的是 <c>ClueList</c> 里的**活对象**（<c>AddRange</c> 按引用）⇒ 绝不就地改：
        /// 只克隆带标记的那几条，其余按引用放进新包（序列化只读，安全）。
        /// </para>
        /// <para>
        /// ⚠ 用 <c>__instance.Underlying.Send</c> 发改写后的包并返回 false：直接再调 <c>__instance.Send</c>
        /// 会重入本 Prefix。
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(ServerGameHostPeerSession), "Send", new[] { typeof(IMessage) })]
        internal static class DisplayTimeOutflowHook
        {
            private static bool Prefix(ServerGameHostPeerSession __instance, IMessage packet)
            {
                if (!FeatureLoader.Enabled<ShadowSabotageFeature>()) return true;

                try
                {
                    int recipient = 0;
                    ServerGamePlayer owner = __instance.Player;
                    if (owner != null && owner.PublicInfo != null)
                    {
                        recipient = owner.PublicInfo.PlayerId;
                    }

                    IMessage rewritten = RewriteOutflow(packet, recipient);
                    if (rewritten == null)
                    {
                        return true;
                    }

                    __instance.Underlying?.Send(rewritten);
                    return false;
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(
                        ex, "破坏点：改写线索显示时间失败（放行原包）。");
                    return true;
                }
            }
        }

        /// <summary>
        /// 0.1.20 线索回放（Timeline）里的镜子：按破坏窗口改写帧内显示。
        /// </summary>
#if TIMELINE   // 回放：0.1.21 已删除该系统的类型
        /// <remarks>
        /// <para>
        /// 只改 <c>TimelineRecorder.Capture</c> 产出的 <c>TimelineFrame</c> 副本，
        /// <b>不碰</b> <c>DeviceInfo.StateList</c> 真值、不广播、不影响实时镜子；
        /// 原有的事后回放（<c>RecordManager</c> 的 tape）完全不受影响。
        /// </para>
        /// <para>
        /// 触发判据只看镜子记录自身的最近更新时刻（<c>StateList[2]</c>）是否落在
        /// <see cref="IsPollutedRecord(int, out bool, out bool)"/> 的污染窗口内 ——
        /// 不看当前是否处于破坏态、不看阶段。
        /// </para>
        /// <para>
        /// 0.1.20 <c>Server.Game.TimelineRecorder.Capture</c> 为 private：
        /// <c>TimelineFrame Capture(int time, Player black, Player victim)</c>（反编译 :186678）。
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(ServerTimelineRecorder), "Capture", new[] { typeof(int), typeof(ServerGamePlayer), typeof(ServerGamePlayer) })]
        internal static class TimelineMirrorHook
        {
            /// <summary>随机身份用（服务端主线程，单实例足够）。</summary>
            private static readonly global::System.Random Rng = new global::System.Random();

            /// <summary>每面镜子上一帧选中的脸（设备 id → PlayerId），保证相邻帧不撞脸。</summary>
            private static readonly global::System.Collections.Generic.Dictionary<int, int> LastFace =
                new global::System.Collections.Generic.Dictionary<int, int>();

            /// <summary>上一帧的时间，用来识别换局（SurviveTime 回退）。</summary>
            private static int _lastFrameTime;

            [HarmonyPostfix]
            private static void Postfix(TimelineFrame __result)
            {
                if (!FeatureLoader.Enabled<ShadowSabotageFeature>()) return;

                try
                {
                    RewriteFrame(__result);
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "破坏点：回放镜子改写失败。");
                }
            }

            private static void RewriteFrame(TimelineFrame frame)
            {
                if (frame == null || frame.Devices == null || frame.Devices.Count == 0)
                {
                    return;
                }

                // 换局：SurviveTime 回退 ⇒ 上一局的脸作废。
                if (frame.Time < _lastFrameTime)
                {
                    LastFace.Clear();
                }

                _lastFrameTime = frame.Time;

                var room = ServerGameRoom.Instance;
                var dm = ServerDeviceManager.Instance;
                if (room == null || room.Players == null || dm == null || dm.Objects == null)
                {
                    return;
                }

                for (int i = 0; i < frame.Devices.Count; i++)
                {
                    var td = frame.Devices[i];
                    if (td == null || td.StateList == null || td.StateList.Count < 3)
                    {
                        continue;
                    }

                    int owner = td.StateList[1];
                    if (owner == 0)
                    {
                        continue;      // 采集那一刻就是灭的 ⇒ 回放里保持灭
                    }

                    int recordTime = td.StateList[2];
                    IsPollutedRecord(recordTime, out bool byIdentity, out bool byClock);
                    if (!byIdentity && !byClock)
                    {
                        continue;
                    }

                    if (!IsMirror(dm, td.DeviceId))
                    {
                        continue;
                    }

                    if (byClock)
                    {
                        // 时间破坏：显示破坏发生的时刻。
                        td.StateList[2] = BreakerStates.Clock.BrokenAt;
                    }

                    if (byIdentity)
                    {
                        int face = PickFace(room, td.DeviceId, owner);
                        if (face != 0)
                        {
                            td.StateList[1] = face;
                        }
                    }
                }
            }

            private static bool IsMirror(ServerDeviceManager dm, int deviceId)
            {
                var devices = dm.Objects;
                for (int i = 0; i < devices.Count; i++)
                {
                    var device = devices[i];
                    if (device != null && device.DeviceInfo != null
                        && device.DeviceInfo.DeviceId == deviceId)
                    {
                        return device is ServerMirror;
                    }
                }

                return false;
            }

            private static int PickFace(ServerGameRoom room, int deviceId, int owner)
            {
                var pool = new global::System.Collections.Generic.List<int>();
                for (int i = 0; i < room.Players.Count; i++)
                {
                    var p = room.Players[i];
                    if (p != null && p.PublicInfo != null
                        && p.IsAlive && !p.IsSpectator && !p.PublicInfo.IsGhost)
                    {
                        pool.Add(p.PublicInfo.PlayerId);
                    }
                }

                if (pool.Count == 0)
                {
                    return owner;
                }

                // 人数 < 5：排除照镜子的人自己；>= 5：不排除（池含所有人）。
                if (pool.Count < 5)
                {
                    pool.Remove(owner);
                }

                if (pool.Count == 0)
                {
                    return owner;
                }

                int last;
                LastFace.TryGetValue(deviceId, out last);

                int pick = pool[Rng.Next(pool.Count)];
                for (int guard = 0; guard < 8 && pool.Count > 1 && pick == last; guard++)
                {
                    pick = pool[Rng.Next(pool.Count)];
                }

                LastFace[deviceId] = pick;
                return pick;
            }
        }

        /// <summary>
        /// 时钟破坏期间的回放：先缓存、修复完成或进入调查阶段时整体打乱后补发。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 只拦 <c>TimelineRecorder.Send</c>；正常时段、身份破坏期一律放行。
        /// 缓存帧的时间在 flush 时统一改写为 <c>BrokenAt + 10k</c>（第一个 = 破坏发生时刻），
        /// 并先挑出「尸体死亡时刻的击杀帧」保留原时间，让原版隐藏窗口逻辑照常工作。
        /// </para>
        /// <para>
        /// flush 时机：① 时钟修复完成；② 进入调查阶段（兜底）；③ 换局由
        /// <c>TimelineRecorder.Clear</c> 清空（对局提前结束则丢弃）。
        /// </para>
        /// <para>
        /// 0.1.20 <c>Server.Game.TimelineRecorder.Send(TimelineFrame)</c> 为 private（反编译 :186667）。
        /// </para>
        /// </remarks>
        internal static class TimelineReplayDelayHook
        {
            private static readonly global::System.Collections.Generic.List<TimelineFrame> Pending =
                new global::System.Collections.Generic.List<TimelineFrame>();

            private static readonly global::System.Random Rng = new global::System.Random();

            /// <summary>flush 期间放行 Send，避免再次被拦截。</summary>
            private static bool _flushing;

            /// <summary>private Send 的反射入口。</summary>
            private static readonly global::System.Reflection.MethodInfo SendMethod =
                AccessTools.Method(typeof(ServerTimelineRecorder), "Send", new[] { typeof(TimelineFrame) });

            /// <summary>Send 拦截入口（由 <see cref="TimelineReplaySendHook"/> 调用）。</summary>
            internal static bool OnSend(TimelineFrame frame)
            {
                if (_flushing) return true;

                try
                {
                    if (frame == null) return true;
                    if (!IsInClockWindow(frame.Time)) return true;

                    Pending.Add(frame);
                    return false;
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "破坏点：回放帧缓存失败（放行）。");
                    return true;
                }
            }

            /// <summary>换局清空（由 <see cref="TimelineReplayClearHook"/> 调用）。</summary>
            internal static void OnClear()
            {
                Pending.Clear();
            }

            internal static void Flush()
            {
                if (Pending.Count == 0)
                {
                    return;
                }

                var frames = new global::System.Collections.Generic.List<TimelineFrame>(Pending);
                Pending.Clear();

                var used = new global::System.Collections.Generic.HashSet<int>();
                var keep = new global::System.Collections.Generic.List<TimelineFrame>();

                int offset = ServerTimelineRecorder.Instance != null ? ServerTimelineRecorder.Instance.Offset : -1;
                int firstRegular;
                int lastRegular;
                ComputeRegularRange(frames, offset, out firstRegular, out lastRegular);

                global::System.Collections.Generic.List<int> deadKeys;
                global::System.Collections.Generic.List<int> hiddenKeys;
                CollectDeathKeys(frames, keep, used, offset, firstRegular, lastRegular, out deadKeys, out hiddenKeys);

                Shuffle(frames);

                int frameIndex = 0;

                // 死亡帧：时间保留（供客户端识别/划掉），内容参与轮选（已被 Shuffle 打乱）。
                for (int i = 0; i < deadKeys.Count && frameIndex < frames.Count; i++)
                {
                    int key = deadKeys[i];
                    if (used.Contains(key))
                    {
                        continue;
                    }

                    var frame = frames[frameIndex++];
                    frame.Time = key;
                    frame.IsKill = true;
                    used.Add(key);
                }

                // 时间破坏期间的偏移与破坏发生时刻一致（不再对齐开局 Offset）；
                // 必须保留原偏移的帧（凶杀 / 哈希锚定）在 keep 列表里，不参与这里。
                int baseTime = BreakerStates.Clock.BrokenAt;

                int k = 0;
                for (; frameIndex < frames.Count; frameIndex++)
                {
                    int time;
                    int guard = 0;
                    do
                    {
                        time = baseTime + 10 * k;
                        k++;
                    }
                    while (used.Contains(time) && guard++ < 500);

                    used.Add(time);
                    frames[frameIndex].Time = time;
                }

                _flushing = true;
                try
                {
                    for (int i = 0; i < keep.Count; i++)
                    {
                        Send(keep[i]);
                    }

                    for (int i = 0; i < frames.Count; i++)
                    {
                        Send(frames[i]);
                    }
                }
                finally
                {
                    _flushing = false;
                }

                global::ClueDetail.Core.Log.Info(
                    $"破坏点：回放补发 {keep.Count + frames.Count} 帧（保留作案帧 {keep.Count}，打乱 {frames.Count}）。");
            }

            private static bool IsInClockWindow(int time)
            {
                return IsPollutedRecord(time, out _, out bool byClock) && byClock;
            }

            /// <summary>
            /// 挑出作案帧（<c>DeadTime</c> 击杀帧）原样保留；并用确定性哈希预测客户端会划掉的
            /// <c>firstHidden</c> / <c>firstHidden+10</c> 时间 key（只占时间，内容参与打乱）。
            /// </summary>
            private static void CollectDeathKeys(
                global::System.Collections.Generic.List<TimelineFrame> frames,
                global::System.Collections.Generic.List<TimelineFrame> keep,
                global::System.Collections.Generic.HashSet<int> used,
                int offset,
                int firstRegular,
                int lastRegular,
                out global::System.Collections.Generic.List<int> deadKeys,
                out global::System.Collections.Generic.List<int> hiddenKeys)
            {
                deadKeys = new global::System.Collections.Generic.List<int>();
                hiddenKeys = new global::System.Collections.Generic.List<int>();

                var room = ServerGameRoom.Instance;
                if (room == null || room.CorpseMetas == null)
                {
                    return;
                }

                foreach (var meta in room.CorpseMetas.Values)
                {
                    if (meta == null || meta.IsReport)
                    {
                        continue;
                    }

                    // 死亡帧：默认三帧全原样保留；关闭开关时才让内容参与轮选。
                    bool keepAll = TimelineKeepAllFrames == null || TimelineKeepAllFrames.Value;
                    if (keepAll)
                    {
                        for (int i = 0; i < frames.Count; i++)
                        {
                            var frame = frames[i];
                            if (frame != null && frame.Time == meta.DeadTime && frame.IsKill)
                            {
                                keep.Add(frame);
                                used.Add(frame.Time);
                                frames.RemoveAt(i);
                                break;
                            }
                        }
                    }
                    else
                    {
                        deadKeys.Add(meta.DeadTime);
                    }

                    // 预测客户端会选的隐藏起点（与客户端 Pick 同一套哈希）。
                    int firstHidden = PredictFirstHidden(meta.DeadTime, meta.VictimId, offset, firstRegular, lastRegular);
                    if (firstHidden <= 0)
                    {
                        continue;
                    }

                    hiddenKeys.Add(firstHidden);
                    hiddenKeys.Add(firstHidden + 10);

                    // 哈希锚定的隐藏帧：原样保留，不参与随机。
                    for (int i = 0; i < frames.Count; i++)
                    {
                        var frame = frames[i];
                        if (frame == null)
                        {
                            continue;
                        }

                        if (frame.Time == firstHidden || frame.Time == firstHidden + 10)
                        {
                            keep.Add(frame);
                            used.Add(frame.Time);
                            frames.RemoveAt(i);
                            i--;
                        }
                    }
                }
            }

            private static int PredictFirstHidden(int deadTime, int victimId, int offset, int first, int last)
            {
                if (offset < 0 || first == int.MaxValue || last == int.MinValue)
                {
                    return 0;
                }

                int num3 = deadTime - Mod(deadTime - offset, 10);
                int firstHidden2 = num3 == deadTime ? deadTime : num3 + 10;

                var list = new global::System.Collections.Generic.List<int>(3);
                if (num3 == deadTime)
                {
                    AddOption(list, deadTime - 10, first, last);
                    AddOption(list, deadTime, first, last);
                }
                else
                {
                    AddOption(list, num3 - 10, first, last);
                    AddOption(list, num3, first, last);
                    AddOption(list, firstHidden2, first, last);
                }

                if (list.Count == 0)
                {
                    return 0;
                }

                return list[Pick(deadTime, victimId, 0, list.Count)];
            }

            private static void AddOption(global::System.Collections.Generic.List<int> options, int time, int first, int last)
            {
                if (time >= first && time + 10 <= last)
                {
                    options.Add(time);
                }
            }

            private static void ComputeRegularRange(
                global::System.Collections.Generic.List<TimelineFrame> frames,
                int offset,
                out int first,
                out int last)
            {
                first = int.MaxValue;
                last = int.MinValue;

                if (offset < 0)
                {
                    return;
                }

                for (int i = 0; i < frames.Count; i++)
                {
                    var frame = frames[i];
                    if (frame == null)
                    {
                        continue;
                    }

                    if (Mod(frame.Time - offset, 10) == 0)
                    {
                        if (frame.Time < first)
                        {
                            first = frame.Time;
                        }

                        if (frame.Time > last)
                        {
                            last = frame.Time;
                        }
                    }
                }
            }

            private static int Pick(int deadTime, int victimId, int salt, int count)
            {
                unchecked
                {
                    int num = (deadTime * 73856093) ^ (victimId * 19349663) ^ (salt * 83492791);
                    int num2 = (num ^ (num >> 13)) * 1540483477;
                    return (int)((uint)(num2 ^ (num2 >> 15)) % (uint)count);
                }
            }

            private static int Mod(int value, int n)
            {
                return (value % n + n) % n;
            }

            private static void Shuffle(global::System.Collections.Generic.List<TimelineFrame> list)
            {
                for (int i = list.Count - 1; i > 0; i--)
                {
                    int j = Rng.Next(i + 1);
                    var tmp = list[i];
                    list[i] = list[j];
                    list[j] = tmp;
                }
            }

            private static void Send(TimelineFrame frame)
            {
                var instance = ServerTimelineRecorder.Instance;
                if (instance != null && SendMethod != null)
                {
                    SendMethod.Invoke(instance, new object[] { frame });
                }
            }
        }

        /// <summary>把时钟破坏的帧缓存注册到回放管线（不再自己挂 Send）。</summary>
        internal static void RegisterClockPipeline()
        {
            global::ClueDetail.Core.Timeline.TimelinePipeline.Register(
                "ShadowSabotage.Clock",
                global::ClueDetail.Core.Timeline.TimelinePipeline.Stage.Transform,
                200,
                (frame, surviveTime, stage) =>
                    !FeatureLoader.Enabled<ShadowSabotageFeature>() || TimelineReplayDelayHook.OnSend(frame));
        }

        /// <summary>换局（<c>TimelineRecorder.Clear</c>）时清空缓存。</summary>
        [HarmonyPatch(typeof(ServerTimelineRecorder), nameof(ServerTimelineRecorder.Clear))]
        internal static class TimelineReplayClearHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                TimelineReplayDelayHook.OnClear();
            }
        }
#endif   // TIMELINE
    }
}
