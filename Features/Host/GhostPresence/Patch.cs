using System;
using ClueDetail.Core;
using Google.Protobuf;
using HarmonyLib;
using Protocol;
using ServerGameHostPeerSession = Server.Game.HostPeerSession;
using ServerGamePlayer = Server.Game.Player;
using ServerGameRoom = Server.Game.GameRoom;

namespace ClueDetail.Features.Host.GhostPresence
{
    /// <summary>
    /// 鬼魂临场感的三个 Harmony 挂点。补丁类必须是 Feature 的**直接**嵌套类型。
    /// </summary>
    internal partial class GhostPresenceFeature
    {
        /// <summary>每秒维护：伪装扫描、显形调度、安魂曲。挂在 private 的 <c>SurvivalTick</c>。</summary>
        [HarmonyPatch(typeof(ServerGameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!FeatureLoader.Enabled<GhostPresenceFeature>())
                {
                    return;
                }

                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "鬼魂彩蛋：每秒维护失败。");
                }
            }
        }

        /// <summary>
        /// 调查阶段每秒维护。⚠️ <c>SurvivalTick</c> 在非生存阶段**直接 return 且不再重排**
        /// （0.1.18 :174300-174302），所以调查阶段它根本不会执行 —— 必须在 <c>DetectiveTick</c>
        /// 上再挂一个，否则「进调查就清伪装」和「调查阶段真鬼显形」全都不会发生。
        /// </summary>
        [HarmonyPatch(typeof(ServerGameRoom), "DetectiveTick")]
        internal static class DetectiveTickHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!FeatureLoader.Enabled<GhostPresenceFeature>())
                {
                    return;
                }

                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "鬼魂彩蛋：调查阶段维护失败。");
                }
            }
        }

        /// <summary>阶段切换：离开生存阶段立即撤掉所有铃铛伪装。</summary>
        /// <remarks>
        /// <c>ChangeGameState</c> 有两个重载（public 单参 / private 双参），必须写参数类型，
        /// 否则 <c>nameof</c> 会抛 Ambiguous match（AGENTS §4.4）。
        /// </remarks>
        [HarmonyPatch(typeof(ServerGameRoom), nameof(ServerGameRoom.ChangeGameState), new[] { typeof(EGameState) })]
        internal static class PhaseHook
        {
            [HarmonyPostfix]
            private static void Postfix(EGameState state)
            {
                if (!FeatureLoader.Enabled<GhostPresenceFeature>())
                {
                    return;
                }

                try
                {
                    OnPhaseChanged(state);
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "鬼魂彩蛋：阶段切换处理失败。");
                }
            }
        }

        /// <summary>
        /// AOI 用 <c>S_ADD_PLAYER</c> 重新介绍某人时，立即补一帧带 <c>IsGhost=true</c> 的
        /// <c>S_SPAWN</c>，否则伪装者会在对方客户端以真身出现。
        /// </summary>
        /// <remarks>
        /// 挂在 <c>HostPeerSession.Send(IMessage)</c>（public，0.1.18 :175626）：服务端给每个玩家的
        /// 下发都经过它，房主本机（<c>LocalLoopbackSink</c>）与远端一视同仁。
        /// 用 <c>__instance.Underlying.Send</c> 补发，避免重入。
        /// </remarks>
        [HarmonyPatch(typeof(ServerGameHostPeerSession), "Send", new[] { typeof(IMessage) })]
        internal static class AddPlayerHook
        {
            [HarmonyPostfix]
            private static void Postfix(ServerGameHostPeerSession __instance, IMessage packet)
            {
                if (!FeatureLoader.Enabled<GhostPresenceFeature>())
                {
                    return;
                }

                if (!DisguiseOn)
                {
                    return;
                }

                try
                {
                    int recipient = 0;
                    ServerGamePlayer owner = __instance.Player;
                    if (owner != null && owner.PublicInfo != null)
                    {
                        recipient = owner.PublicInfo.PlayerId;
                    }

                    OnAddPlayerSent(packet, __instance.Underlying, recipient);
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "鬼魂彩蛋：补发伪装失败。");
                }
            }
        }
    }
}
