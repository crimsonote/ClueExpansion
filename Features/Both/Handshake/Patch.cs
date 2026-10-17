using ClueDetail.Core;
using Google.Protobuf;
using HarmonyLib;
using GameHostPeerSession = Server.Game.HostPeerSession;

namespace ClueDetail.Features.Both.Handshake
{
    internal static partial class HandshakeFeature
    {
        /// <summary>
        /// 客户端：给即将发出的白名单包打上能力标记。
        /// </summary>
        /// <remarks>
        /// 挂在 <c>HostPeerSession.Send(IMessage)</c> 上 —— 这是 <c>Managers.Network.GameServer</c>
        /// 的运行时实现（0.1.18 :175626），客户端所有出站包都经它。
        /// Prefix 里只做一次类型判断，非白名单包直接返回，开销可忽略。
        /// </remarks>
        [HarmonyPatch(typeof(GameHostPeerSession), "Send", new[] { typeof(IMessage) })]
        internal static class ClientStampHook
        {
            private static void Prefix(IMessage packet)
            {
                if (RoomState.IsHost)
                {
                    return;
                }

                ClientCapability.StampOutgoing(packet);
            }
        }

        /// <summary>
        /// 服务端：从统一收包点读回标记并登记玩家。
        /// </summary>
        /// <remarks>
        /// 挂在 <c>PacketQueue.Push(Packet)</c>（0.1.18 :45379）而不是具体某个包的 handler：
        /// <list type="bullet">
        /// <item>服务端收到**任何**包都会经这里入队（<c>ServerSession.OnRecvPacket</c> 的回调），
        /// 所以白名单里任意一个包先到都能登记；</item>
        /// <item>不必为每个包各写一个补丁。</item>
        /// </list>
        /// <para>
        /// <c>packet.Session</c> 是 <c>ServerSession.Wrapped</c>（= <c>HostPeerSession</c>），
        /// 房主自己的回环包则是 <c>LocalLoopbackSink</c> —— 后者不是 <c>HostPeerSession</c>，
        /// 天然被排除，不需要额外判重。
        /// </para>
        /// <para>
        /// 这里刻意**只读不写**：<c>PacketQueue.Push</c> 在多线程上下文里被调用（内部有 lock），
        /// 而 <c>Player</c> 的访问跟在主线程处理包时并无锁保护，所以只做"登记一个 id"这种纯托管操作。
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(PacketQueue), nameof(PacketQueue.Push))]
        internal static class ServerReadHook
        {
            private static void Postfix(Packet packet)
            {
                if (packet == null || !RoomState.IsHost)
                {
                    return;
                }

                if (!(packet.Session is GameHostPeerSession peer) || peer.Player == null)
                {
                    // Player 还没绑定（例如刚进房的那一包）—— 后面的包还会再来。
                    return;
                }

                ClientCapability.TryRead(packet.Pkt, peer.Player.PublicInfo.PlayerId);
            }
        }
    }
}
