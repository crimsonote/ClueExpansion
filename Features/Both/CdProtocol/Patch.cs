using ClueDetail.Core;
using ClueDetail.Core.Net;
using DummyClient;
using Google.Protobuf;
using HarmonyLib;
using GameHostPeerSession = Server.Game.HostPeerSession;

namespace ClueDetail.Features.Both.CdProtocol
{
    internal static partial class CdProtocolFeature
    {
        /// <summary>
        /// P2P 出站口（<c>Managers.Network.GameServer</c> 的运行时实现就是它）：
        /// 只把自家消息改成裸字节发，其余一律放行。
        /// </summary>
        [HarmonyPatch(typeof(SteamP2PSession), "Send", new[] { typeof(IMessage) })]
        internal static class P2PSendHook
        {
            private static bool Prefix(SteamP2PSession __instance, IMessage packet)
            {
                return !CdWire.TryIntercept(__instance, packet);
            }
        }

        /// <summary>
        /// 另一种会话实现的出站口（P2P 之外的路径）。
        /// 它继承 <c>Session</c>，裸字节出口是 <c>Send(ArraySegment&lt;byte&gt;)</c>。
        /// </summary>
        [HarmonyPatch(typeof(ServerSession), "Send", new[] { typeof(IMessage) })]
        internal static class ServerSendHook
        {
            private static bool Prefix(ServerSession __instance, IMessage packet)
            {
                return !CdWire.TryIntercept(__instance, packet);
            }
        }

        /// <summary>
        /// 客户端侧：借着"每个出站包都会经过这里"，在第一个包时顺带发一条能力声明。
        /// </summary>
        /// <remarks>
        /// 之所以能这么做，正是自定义协议相对未知字段的好处：**时机由我们定**，
        /// 不需要再找某个必然发送的原版包去搭载。
        /// </remarks>
        [HarmonyPatch(typeof(GameHostPeerSession), "Send", new[] { typeof(IMessage) })]
        internal static class ClientHelloHook
        {
            private static void Prefix(GameHostPeerSession __instance, IMessage packet)
            {
                if (RoomState.IsHost || packet is CdMessage)
                {
                    return;
                }

                ClientCapability.EnsureHello(__instance);
            }
        }
    }
}
