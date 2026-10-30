using System;
using System.Reflection;
using ClueDetail.Core;
using HarmonyLib;
using Protocol;
using ServerGamePlayer = Server.Game.Player;
using ServerGameRoom = Server.Game.GameRoom;
using ServerTimeManager = Server.Game.TimeManager;

namespace ClueDetail.Features.Host.MissionBacklash
{
    /// <summary>
    /// 任务倒扣的两个 Harmony 挂点。补丁类必须是 Feature 的**直接**嵌套类型（只扫一层）。
    /// </summary>
    internal partial class MissionBacklashFeature
    {
        /// <summary>
        /// 拦截 <c>MissionManager.ClearMission</c>（0.1.18 :169575）：
        /// 只有「黑方完成 + 任务真的被清掉」才记账。
        /// </summary>
        /// <remarks>
        /// <c>MissionManager</c> 在发行程序集里是 internal（:169148），不能用 <c>typeof</c>，
        /// 按 AGENTS §4.5 走 <c>AccessTools.TypeByName</c> + <c>[HarmonyTargetMethod]</c>。
        /// <para>
        /// <c>ClearMission</c> 有三条提前 return 的路径（进度已满 :169577、任务已不在列表 :169582），
        /// 所以用 Prefix 记下「进方法前任务在不在」，Postfix 再确认「出来之后没了」才算一次完成。
        /// </para>
        /// </remarks>
        [HarmonyPatch]
        internal static class MissionClearHook
        {
            [HarmonyTargetMethod]
            private static MethodBase Target()
            {
                Type type = AccessTools.TypeByName("Server.Game.MissionManager");
                if (type == null)
                {
                    global::ClueDetail.Core.Log.Warn(
                        "任务倒扣：找不到 Server.Game.MissionManager，补丁不会挂载。");
                    return null;
                }

                return AccessTools.Method(
                    type,
                    "ClearMission",
                    new[] { typeof(ESchoolMission), typeof(ServerGamePlayer), typeof(bool) });
            }

            [HarmonyPrefix]
            private static void Prefix(object __instance, ESchoolMission mission, ref bool __state)
            {
                if (!FeatureLoader.Enabled<MissionBacklashFeature>())
                {
                    __state = false;
                    return;
                }

                __state = MissionBridge.HasMission(__instance, (int)mission);
            }

            [HarmonyPostfix]
            private static void Postfix(
                object __instance, ESchoolMission mission, ServerGamePlayer completer, bool __state)
            {
                if (!FeatureLoader.Enabled<MissionBacklashFeature>())
                {
                    return;
                }

                // 进方法前就不在列表里（重复调用 / 已清）→ 不是这次完成。
                if (!__state)
                {
                    return;
                }

                // 出来之后还在列表里 → 原方法提前 return 了（比如进度已满）。
                if (MissionBridge.HasMission(__instance, (int)mission))
                {
                    return;
                }

                if (completer == null)
                {
                    return;
                }

                // 黑方全体：Black（黑杀手）与 Dark（黑幕），0.1.18 :178434 的原版口径。
                EPlayerColor color = completer.Color;
                if (color != EPlayerColor.Black && color != EPlayerColor.Dark)
                {
                    return;
                }

                try
                {
                    Record(__instance, mission);
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, $"任务倒扣：记录 {mission} 失败。");
                }
            }
        }

        /// <summary>
        /// 每秒一次的结算检查。挂在 <c>GameRoom.SurvivalTick</c>（private，0.1.18 :174298）。
        /// </summary>
        /// <remarks>
        /// 选它而不是 <c>TimeManager.Tick</c>：<c>SurvivalTick</c> 每秒无条件重排一次
        /// （:174327），而 <c>TimeManager.Tick</c> 在非生存阶段不推进 —— 两者都只在该阶段有意义，
        /// 但前者还能顺带拿到 <c>GameRoom.State</c> 做阶段守卫（:174300）。
        /// </remarks>
        [HarmonyPatch(typeof(ServerGameRoom), "SurvivalTick")]
        internal static class SurvivalTickHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!FeatureLoader.Enabled<MissionBacklashFeature>())
                {
                    return;
                }

                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "任务倒扣：每秒维护失败。");
                }
            }
        }

        /// <summary>
        /// 每局开局重置记账桶。挂在 <c>TimeManager.ResetSurvival</c>（public，0.1.18 :181784）。
        /// </summary>
        /// <remarks>
        /// 为什么必须有这个挂点：<c>SurvivalTick</c> 在非生存阶段**直接 return 且不再重排**
        /// （:174300-174303），所以每秒钩子在阶段结束后就停摆了，没机会在阶段外清桶。
        /// 而 <c>SurviveTime</c> 每局都从 420 重新开始（:181787），如果上一局恰好在
        /// 「刚对齐桶起点」就结束（没有跨过任何结算点），新一局的 <c>now == BucketStart</c>
        /// 不满足 <c>now &lt; BucketStart</c>，旧账就会被带进新一局。
        /// <para>
        /// <c>ResetSurvival</c> 是开局与回大厅都会走的公共收口，在这里清一次最稳；
        /// <see cref="Tick"/> 里的 <c>now &lt; BucketStart</c> 仍保留，作为挂点失效时的兜底。
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(ServerTimeManager), nameof(ServerTimeManager.ResetSurvival))]
        internal static class NewRoundHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!FeatureLoader.Enabled<MissionBacklashFeature>())
                {
                    return;
                }

                Reset();
                global::ClueDetail.Core.Log.Info("任务倒扣：新一局（ResetSurvival），已清空未结算的记账。");
            }
        }
    }
}
