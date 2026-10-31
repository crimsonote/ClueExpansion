using System;
using ClueDetail.Core;
using HarmonyLib;
using ServerGameRoom = Server.Game.GameRoom;
using ServerTimeManager = Server.Game.TimeManager;

namespace ClueDetail.Features.Host.FlowerScene
{
    /// <summary>
    /// 花瓣场景的两个挂点：每局开局复位、进入生存阶段后铺一次。
    /// </summary>
    internal partial class FlowerSceneFeature
    {
        /// <summary>每局开局（<c>ResetSurvival</c>）复位"本局已铺"。</summary>
        [HarmonyPatch(typeof(ServerTimeManager), nameof(ServerTimeManager.ResetSurvival))]
        internal static class RoundResetHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!FeatureLoader.Enabled<FlowerSceneFeature>())
                {
                    return;
                }

                try
                {
                    Reset();
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "花瓣场景：开局复位失败。");
                }
            }
        }

        /// <summary>生存阶段每秒检查一次：还没铺就铺（每局一次）。</summary>
        [HarmonyPatch(typeof(ServerGameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!FeatureLoader.Enabled<FlowerSceneFeature>())
                {
                    return;
                }

                try
                {
                    TryScatter();
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "花瓣场景：铺设检查失败。");
                }
            }
        }
    }
}
