using System.Collections.Generic;
using Google.Protobuf;
using Protocol;

namespace ClueDetail.Core
{
    /// <summary>
    /// 客户端能力登记（服务端侧）：哪些玩家装了本模块。
    /// </summary>
    /// <remarks>
    /// 上报走的是和线索详情同一条通道 —— protobuf 未知字段。客户端在若干**原版就有**的
    /// 出站包上附带一个能力版本号（字段号 2），服务端从统一收包点读回来。
    /// 未装本模块的客户端不会写这个字段，服务端读到的就是 0 —— 天然区分，不需要往返协商。
    /// <para>
    /// 为什么不自己发明一个"只有 mod 才发的请求"：做不到。自定义消息发不出去 ——
    /// <c>ServerSession.Send</c> 用 <c>Enum.Parse(PacketID, packet.Descriptor.Name)</c> 反解 opcode
    /// （0.1.18 :162551），名字不在 <c>PacketID</c> 枚举里会直接抛异常；
    /// <c>PacketManager.RegisterMake</c> 只能注册**接收**。
    /// 所以"只有 mod 才带的东西"就是这里的等效物：原版包 + 未知字段。
    /// </para>
    /// <para>
    /// 登记结果只用于统计与提示，**不参与决策**：线索详情字段对原版客户端本来就无害，
    /// 所以下发始终是全员广播，不必按能力分包。
    /// </para>
    /// </remarks>
    internal static class ClientCapability
    {
        /// <summary>标记写在出站包上的字段号。这些包原本都只有 1 号字段在用。</summary>
        public const int HelloFieldNumber = 2;

        /// <summary>能力版本，将来上报内容变化就 +1。</summary>
        public const int Version = 1;

        private static readonly HashSet<int> Supporters = new HashSet<int>();

        /// <summary>
        /// 承载标记的包白名单。
        /// </summary>
        /// <remarks>
        /// 刻意**多处冗余**：最早的 <c>C_ENTER_GAME</c> 到达服务端时
        /// <c>HostPeerSession.Player</c> 往往还没绑定（Player 是在处理该包的过程中创建的），
        /// 单靠它可能扑空；登记是幂等的，所以后面任意一个包补上都算。
        /// 同时刻意**不含 C_MOVE**（每帧发包，没必要每帧多背两个字节）。
        /// </remarks>
        private static bool CanCarryHello(IMessage packet)
        {
            return packet is C_ENTER_GAME
                || packet is C_READY
                || packet is C_SCAN_DEVICE
                || packet is C_SUBMIT_PROPOSITION;
        }

        /// <summary>给即将发出的包打上能力标记（客户端侧调用）。</summary>
        public static void StampOutgoing(IMessage packet)
        {
            if (packet == null || !CanCarryHello(packet))
            {
                return;
            }

            if (UnknownFields.HasValue(packet, HelloFieldNumber))
            {
                return;
            }

            UnknownFields.SetValue(packet, HelloFieldNumber, Version);
        }

        /// <summary>从收到的包里读回标记并登记（服务端侧调用）。</summary>
        public static void TryRead(IMessage packet, int playerId)
        {
            if (packet == null || playerId == 0 || !CanCarryHello(packet))
            {
                return;
            }

            if (UnknownFields.TryGetValue(packet, HelloFieldNumber, out int version) && version > 0)
            {
                Mark(playerId);
            }
        }

        private static void Mark(int playerId)
        {
            if (Supporters.Add(playerId))
            {
                Log.Info($"客户端能力上报：玩家 {playerId} 装有本模块（累计 {Supporters.Count} 人）。");
            }
        }

        public static bool IsSupporter(int playerId)
        {
            return Supporters.Contains(playerId);
        }

        public static int Count => Supporters.Count;

        public static void Clear()
        {
            Supporters.Clear();
        }
    }
}
