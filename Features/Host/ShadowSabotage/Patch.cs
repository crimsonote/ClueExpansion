using ClueDetail.Core;
using HarmonyLib;
using Protocol;
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
                    }
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "破坏点：离开 Survive 收尾失败。");
                }
            }
        }
    }
}
