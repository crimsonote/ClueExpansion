using ClueDetail.Core;
using Google.Protobuf;
using HarmonyLib;
using Protocol;
using ServerDevice = Server.Game.Device;
using ItemManager = Server.Game.ItemManager;
using ServerDeviceManager = Server.Game.DeviceManager;
using ServerGamePlayer = Server.Game.Player;
using ServerGameRoom = Server.Game.GameRoom;

namespace ClueDetail.Features.Host.ShadowSabotage
{
    /// <summary>
    /// 隐蔽破坏点的全部 Harmony 挂点。补丁类必须是 Feature 的**直接**嵌套类型（只扫一层）。
    /// </summary>
    internal static partial class ShadowSabotageFeature
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
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "破坏点：离开 Survive 收尾失败。");
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
                if (state != Define.EWhatState.Normal)
                {
                    return;                 // 角色线索豁免
                }

                if (!IdentifyPollutionOn)
                {
                    return;
                }

                state = Define.EWhatState.Anonymity;
            }
        }

        /// <summary>
        /// 时间污染（实时广播那条路）：把即将发出去的线索时间改成时钟被破坏的时刻。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 为什么安全：原版 <c>RecordLastUsingPlayer</c> 是**先 <c>Clone()</c> 再 <c>Broadcast</c>**
        /// （0.1.18 :166415-166436），包里是副本，改它不碰服务端的 <c>ClueList</c>，
        /// 也就不会污染 30 秒去重窗口。
        /// </para>
        /// <para>
        /// <c>S_REMOVE_CLUE</c> 必须一起改：去重分支是「先移除旧副本、再添加新副本」，
        /// 如果只改 add，客户端会按**真实时间**找不到那条要移除的线索（它手里的是冻结时间）
        /// ⇒ 旧线索残留。
        /// </para>
        /// <para>
        /// ⚠ <c>[HarmonyPriority(Priority.Last)]</c> 不能省：<c>RecordDetail.BroadcastHook</c>
        /// 用 <c>(PlayerId, Time, RoomId)</c> 去旁路表查详细活动 —— 我们必须**等它查完**再改 Time，
        /// 否则它永远查不到（键已经变形）。
        /// </para>
        /// </remarks>
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(ServerGameRoom), "Broadcast", new[] { typeof(IMessage), typeof(int) })]
        internal static class TimePolluteBroadcastHook
        {
            [HarmonyPrefix]
            private static void Prefix(IMessage packet)
            {
                if (!TimePollutionOn)
                {
                    return;
                }

                if (packet is S_ADD_CLUE add)
                {
                    ApplyFrozenTime(add.Info);
                    return;
                }

                if (packet is S_REMOVE_CLUE remove)
                {
                    ApplyFrozenTime(remove.Info);
                }
            }
        }

        /// <summary>
        /// 时间污染（扫描下发那条路）：**整替**原版 <c>Device.Scan</c>。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 原版实现是按**引用** <c>AddRange(ClueList)</c>（0.1.18 :166318）——
        /// 直接改那些对象的 <c>Time</c> 会污染服务端内部数据（连带影响 30 秒去重窗口），
        /// 所以这里克隆一份再改，零副作用。
        /// </para>
        /// <para>
        /// ⚠ <c>[HarmonyPriority(Priority.Last)]</c>：必须**晚于**
        /// <c>RecordDetail.ScanHook</c>（它往 <c>ClueList</c> 的对象上补未知字段「详细活动」）。
        /// 我们克隆得比它早的话，发出去的线索就没有详细活动了。
        /// </para>
        /// <para>
        /// 返回 <c>false</c> 只跳过原方法体，其它 Prefix 照常执行（Harmony ≥ 2.2）。
        /// </para>
        /// </remarks>
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(ServerDevice), nameof(ServerDevice.Scan))]
        internal static class TimePolluteScanHook
        {
            [HarmonyPrefix]
            private static bool Prefix(ServerDevice __instance, ServerGamePlayer player)
            {
                if (!TimePollutionOn || __instance == null)
                {
                    return true;
                }

                var list = __instance.ClueList;
                if (list == null || list.Count == 0)
                {
                    return true;
                }

                var packet = new S_SCAN_DEVICE();
                int frozen = FrozenTime;
                for (int i = 0; i < list.Count; i++)
                {
                    var info = list[i];
                    if (info == null || info.Destroyed)
                    {
                        continue;               // 与原版 Where(!Destroyed) 对齐
                    }

                    var copy = info.Clone();
                    copy.Time = frozen;
                    packet.InfoList.Add(copy);
                }

                if (player != null && player.Session != null)
                {
                    player.Session.Send(packet);
                }

                return false;
            }
        }
    }
}
