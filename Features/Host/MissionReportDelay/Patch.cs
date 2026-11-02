using System;
using System.Reflection;
using ClueDetail.Core;
using HarmonyLib;
using Protocol;
using ServerGameRoom = Server.Game.GameRoom;
using ServerTimeManager = Server.Game.TimeManager;

namespace ClueDetail.Features.Host.MissionReportDelay
{
    /// <summary>
    /// 任务满延迟的 Harmony 挂点。补丁类必须是 Feature 的**直接**嵌套类型（装载器只扫一层）。
    /// </summary>
    internal partial class MissionReportDelayFeature
    {
        /// <summary>
        /// <c>MissionManager.TriggerAllClearEnd</c>（0.1.18 :169787，private，类型 internal）：
        /// 唯一收口。拦它 = 同时阻止“报告尸体”和“白方胜利”。
        /// </summary>
        [HarmonyPatch]
        internal static class TriggerAllClearEndHook
        {
            [HarmonyTargetMethod]
            private static MethodBase Target()
            {
                ResolveReflection();
                if (_triggerMethod == null)
                {
                    global::ClueDetail.Core.Log.Warn(
                        "任务报告延迟：找不到 MissionManager.TriggerAllClearEnd，补丁不会挂载。");
                    return null;
                }

                return _triggerMethod;
            }

            [HarmonyPrefix]
            private static bool Prefix()
            {
                if (!FeatureLoader.Enabled<MissionReportDelayFeature>()) return true;

                try
                {
                    return ShouldRunOriginal();
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "任务报告延迟：判断失败（放行原版）。");
                    return true;
                }
            }
        }

        /// <summary>
        /// <c>TimeManager.UpdateRemainTime</c>（0.1.18 :181703）：任何来源的加/扣时间都压回冻结值。
        /// </summary>
        /// <remarks>
        /// <c>Priority.Low</c>：跑在其它改时间的补丁之后，保证最终值是冻结值。
        /// </remarks>
        [HarmonyPriority(Priority.Low)]
        [HarmonyPatch(typeof(ServerTimeManager), nameof(ServerTimeManager.UpdateRemainTime))]
        internal static class FreezeUpdateRemainTimeHook
        {
            [HarmonyPostfix]
            private static void Postfix(ServerTimeManager __instance)
            {
                if (!FeatureLoader.Enabled<MissionReportDelayFeature>()) return;
                if (!_freeze) return;
                ApplyFreeze(__instance);
            }
        }

        /// <summary>
        /// <c>TimeManager.TickSurvive</c>（0.1.18 :181688）：正常情况下 <c>AllClear</c> 已让
        /// <c>SurvivalTick</c> 停摆；这是保险，防止别的路径继续 Tick 时倒计时往下掉。
        /// </summary>
        [HarmonyPriority(Priority.Low)]
        [HarmonyPatch(typeof(ServerTimeManager), nameof(ServerTimeManager.TickSurvive))]
        internal static class FreezeTickSurviveHook
        {
            [HarmonyPostfix]
            private static void Postfix(ServerTimeManager __instance)
            {
                if (!FeatureLoader.Enabled<MissionReportDelayFeature>()) return;
                if (!_freeze) return;
                ApplyFreeze(__instance);
            }
        }

        /// <summary>
        /// <c>GameRoom.ChangeGameState(EGameState, EGameState)</c>（0.1.18 :173057）：
        /// 任何阶段切换都清一次状态（手动报告 → 调查阶段时尤其重要）。
        /// </summary>
        [HarmonyPatch(typeof(ServerGameRoom), "ChangeGameState", new[] { typeof(EGameState), typeof(EGameState) })]
        internal static class StateChangeHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!FeatureLoader.Enabled<MissionReportDelayFeature>()) return;
                OnStateChanged();
            }
        }

        /// <summary><c>TimeManager.ResetSurvival</c>（0.1.18 :181784）：换局清空。</summary>
        [HarmonyPatch(typeof(ServerTimeManager), nameof(ServerTimeManager.ResetSurvival))]
        internal static class RoundResetHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!FeatureLoader.Enabled<MissionReportDelayFeature>()) return;
                Reset(false);
            }
        }

        /// <summary><c>GameRoom.FullReset</c>（0.1.18 :172984）：房间完全复位。</summary>
        [HarmonyPatch(typeof(ServerGameRoom), nameof(ServerGameRoom.FullReset))]
        internal static class FullResetHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!FeatureLoader.Enabled<MissionReportDelayFeature>()) return;
                Reset(false);
            }
        }
    }
}
