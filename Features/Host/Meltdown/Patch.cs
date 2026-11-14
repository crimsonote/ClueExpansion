using ClueDetail.Core;
using HarmonyLib;
using Protocol;
using ServerGameRoom = Server.Game.GameRoom;
using ServerTimeManager = Server.Game.TimeManager;

namespace ClueDetail.Features.Host.Meltdown
{
    internal partial class MeltdownFeature
    {
        /// <summary>
        /// <c>TimeManager.TickSurvive</c>：每秒把 <c>RemainTime</c> 扣 1。
        /// 断电期间在 Prefix 里按倍率预调，让原方法扣完 / 广播出目标值。
        /// </summary>
        [HarmonyPatch(typeof(ServerTimeManager), nameof(ServerTimeManager.TickSurvive))]
        internal static class TickSurviveHook
        {
            [HarmonyPrefix]
            private static void Prefix()
            {
                if (!FeatureLoader.Enabled<MeltdownFeature>())
                {
                    return;
                }

                MeltdownLogic.BeforeTick();
            }
        }

        /// <summary>
        /// <c>GameRoom.SurvivalTick</c>（private）：每秒检查一次提示时机。
        /// </summary>
        [HarmonyPatch(typeof(ServerGameRoom), "SurvivalTick")]
        internal static class SurvivalTickHook
        {
            [HarmonyPostfix]
            private static void Postfix(ServerGameRoom __instance)
            {
                if (!FeatureLoader.Enabled<MeltdownFeature>())
                {
                    return;
                }

                if (__instance == null || __instance.State != EGameState.Survive)
                {
                    return;
                }

                MeltdownLogic.TickNotice(__instance);
            }
        }

        /// <summary><c>GameRoom.GameOver</c>：记录本局是否「时间流空」。</summary>
        [HarmonyPatch(typeof(ServerGameRoom), nameof(ServerGameRoom.GameOver))]
        internal static class GameOverHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!FeatureLoader.Enabled<MeltdownFeature>())
                {
                    return;
                }

                MeltdownLogic.RecordGameOver();
            }
        }

        /// <summary>
        /// <c>GameRoom.ChangeGameState</c>：进入 Survive 时开始新一局统计；
        /// 回到 Lobby 时清空本局状态。
        /// </summary>
        [HarmonyPatch(typeof(ServerGameRoom), nameof(ServerGameRoom.ChangeGameState), new[] { typeof(EGameState) })]
        internal static class GameStateHook
        {
            [HarmonyPostfix]
            private static void Postfix(EGameState state)
            {
                if (!FeatureLoader.Enabled<MeltdownFeature>())
                {
                    return;
                }

                if (state == EGameState.Survive)
                {
                    MeltdownLogic.BeginRound();
                }
                else if (state == EGameState.Lobby)
                {
                    MeltdownState.Reset();
                }
            }
        }
    }
}
