using ClueDetail.Core;
using HarmonyLib;
using Protocol;
using ServerGameRoom = Server.Game.GameRoom;

namespace ClueDetail.Features.Host.FlowerCarpet
{
    /// <summary>
    /// 彩蛋的两个挂点：阶段切换（进调查）与每秒维护（任务完成度）。
    /// </summary>
    internal partial class FlowerCarpetFeature
    {
        /// <summary>
        /// 阶段切换：进入调查阶段时撒花；回到生存 / 大厅时复位"本局已撒"。
        /// </summary>
        /// <remarks>
        /// 为什么挂在 <c>ChangeGameState</c> 而不是 <c>StartDetective</c>：
        /// 后者是 private、且只在调查开场那一条链上跑；前者是所有阶段切换的唯一收口，
        /// 顺便还能拿到"回到 Survive"这个换局信号来复位标记。
        /// </remarks>
        [HarmonyPatch(typeof(ServerGameRoom), nameof(ServerGameRoom.ChangeGameState), new[] { typeof(EGameState) })]
        internal static class StateHook
        {
            [HarmonyPostfix]
            private static void Postfix(EGameState state)
            {
                if (!FeatureLoader.Enabled<FlowerCarpetFeature>()) return;

                try
                {
                    if (state == EGameState.Detective)
                    {
                        if (TriggerOnDetectiveValue)
                        {
                            Scatter();
                        }

                        return;
                    }

                    // 回到生存 / 大厅 = 新的一局或还没开局 ⇒ 复位，允许下一局再撒。
                    if (state == EGameState.Survive || state == EGameState.Lobby)
                    {
                        Reset();
                    }
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "彩蛋：阶段切换处理失败。");
                }
            }
        }

        /// <summary>
        /// 每秒检查任务完成度（只在 Survive 阶段被原版调用）。
        /// </summary>
        [HarmonyPatch(typeof(ServerGameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!FeatureLoader.Enabled<FlowerCarpetFeature>()) return;

                try
                {
                    CheckMissionProgress();
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "彩蛋：任务进度检查失败。");
                }
            }
        }
    }
}
