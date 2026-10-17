using System;
using System.Collections;
using System.Reflection;
using DummyClient;
using Google.Protobuf;
using Server.Game;

namespace ClueDetail.Core.Net
{
    /// <summary>
    /// 自定义协议的收发装配点：组包、代发、注册、分发。
    /// </summary>
    /// <remarks>
    /// <b>不让原版坏掉，靠这四条：</b>
    /// <list type="number">
    /// <item><b>只拦自家消息</b>：<see cref="TryIntercept"/> 第一个判断就是
    /// <c>packet is CdMessage</c>，原版消息一律返回 false 走原路径，一个字节都不改；</item>
    /// <item><b>opcode 取高位段</b>（<see cref="CdOpcode.Base"/> = 61000），原版按顺序递增到不了；</item>
    /// <item><b>注册前查占用</b>：<c>RegisterMake</c> 是覆盖语义，撞车就放弃注册并报错，绝不覆盖；</item>
    /// <item><b>包格式逐字节复刻原版</b>：<c>int32 总长 | uint16 opcode | body</c>
    /// （0.1.18 :163403-163406 / :162556-162560），这样装了两端才认得出。</item>
    /// </list>
    /// <para>
    /// 未装本模块的一端收到这些包时，<c>PacketManager.OnRecvPacket</c> 查不到 opcode
    /// 会直接 <c>return</c>（:44894）—— 静默丢弃，不报错、不踢人、不影响任何原版逻辑。
    /// </para>
    /// </remarks>
    internal static class CdWire
    {
        private static readonly FieldInfo MakeFuncField =
            typeof(PacketManager).GetField("_makeFunc", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>已成功注册的消息数（自检用）。</summary>
        public static int Registered { get; private set; }

        /// <summary>注册失败的消息数（opcode 撞车或拿不到注册表）。</summary>
        public static int Failed { get; private set; }

        // ── 组包 ────────────────────────────────────────────────────────────

        public static byte[] Pack(CdMessage message)
        {
            int size = message.CalculateSize();
            byte[] buffer = new byte[size + 6];
            Array.Copy(BitConverter.GetBytes(size + 6), 0, buffer, 0, 4);
            Array.Copy(BitConverter.GetBytes(message.Opcode), 0, buffer, 4, 2);
            Array.Copy(message.ToByteArray(), 0, buffer, 6, size);
            return buffer;
        }

        /// <summary>把自家消息改道成裸字节发出。返回 true 表示"已代发，原方法不要再跑"。</summary>
        public static bool TryIntercept(object session, IMessage packet)
        {
            if (!(packet is CdMessage message))
            {
                return false;
            }

            byte[] bytes = Pack(message);

            switch (session)
            {
                case SteamP2PSession p2p:
                    p2p.SendBytes(bytes);
                    return true;

                case ServerSession server:
                    server.Send(new ArraySegment<byte>(bytes));
                    return true;

                default:
                    Log.Warn($"自定义协议包发不出去：未知的会话类型 {session?.GetType().Name}。");
                    // 仍然吞掉：放回原路径只会让原版去 Enum.TryParse 失败并打一条 LogError。
                    return true;
            }
        }

        /// <summary>给某个会话发一条自家消息（主动通知，不受"寄生"限制）。</summary>
        /// <remarks>
        /// 刻意走**正常的 Send 链路**而不是直接调 <see cref="TryIntercept"/> ——
        /// 让拦截只有一个入口（Prefix 补丁），与"客户端自发一条包"的行为完全一致。
        /// </remarks>
        public static void Send(IPacketSink session, CdMessage message)
        {
            if (session == null || message == null)
            {
                return;
            }

            session.Send(message);
        }

        // ── 注册 ────────────────────────────────────────────────────────────

        public static void RegisterAll()
        {
            Registered = 0;
            Failed = 0;

            Register<CdHello>(CdOpcode.Hello, OnHello);
            Register<CdState>(CdOpcode.State, OnState);

            if (Failed > 0)
            {
                Log.Error($"自定义协议注册不完整：成功 {Registered}，失败 {Failed} —— " +
                          "失败的几条不会工作，但原版不受影响（刻意不覆盖已有 opcode）。");
            }
            else
            {
                Log.Info($"自定义协议已注册 {Registered} 条（opcode 段 {CdOpcode.Base} 起）。");
            }
        }

        private static void Register<T>(ushort opcode, Action<IPacketSink, Packet> handler)
            where T : CdMessage, new()
        {
            if (!IsOpcodeFree(opcode))
            {
                Failed++;
                Log.Error($"opcode {opcode} 已被占用，放弃注册 —— 绝不覆盖原版" +
                          "（若是版本升级导致撞车，请把 CdOpcode.Base 整体挪段）。");
                return;
            }

            PacketManager.Instance.RegisterMake<T>((PacketID)opcode);
            PacketManager.Instance.RegisterHandler((PacketID)opcode, handler);
            Registered++;
        }

        /// <summary>该 opcode 在收包表里是否空闲。拿不到表就返回 false（宁可不注册）。</summary>
        private static bool IsOpcodeFree(ushort opcode)
        {
            try
            {
                if (MakeFuncField?.GetValue(PacketManager.Instance) is IDictionary map)
                {
                    return !map.Contains(opcode);
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "检查 opcode 占用情况失败");
            }

            return false;
        }

        // ── 收包 ────────────────────────────────────────────────────────────

        private static void OnHello(IPacketSink session, Packet packet)
        {
            if (!(packet.Pkt is CdHello hello) || !RoomState.IsHost)
            {
                return;
            }

            int playerId = PlayerIdOf(session);
            if (playerId != 0)
            {
                ClientCapability.Mark(playerId, hello.Version);
            }

            // 不论认不认识他都回一条当前状态：未装本模块的一端收到会静默丢弃。
            Send(session, MakeState());
        }

        private static void OnState(IPacketSink session, Packet packet)
        {
            if (packet.Pkt is CdState state)
            {
                RoomState.ApplyRemote(state.Enabled != 0);
            }
        }

        /// <summary>当前房主侧状态包。</summary>
        public static CdState MakeState()
        {
            return new CdState
            {
                Enabled = RoomState.HostBroadcast ? 1 : 0,
                HostVersion = ClientCapability.Version,
            };
        }

        /// <summary>把状态广播给房间里所有玩家（房主改开关时立刻通知，不必等下一次线索包）。</summary>
        public static void BroadcastState()
        {
            if (!RoomState.IsHost)
            {
                return;
            }

            var state = MakeState();
            var room = GameRoom.Instance;
            if (room?.Players == null)
            {
                return;
            }

            for (int i = 0; i < room.Players.Count; i++)
            {
                var player = room.Players[i];
                if (player?.Session != null)
                {
                    Send(player.Session, state);
                }
            }
        }

        /// <summary>从会话反查玩家号；服务端视角的客户端会话是 <c>HostPeerSession</c>。</summary>
        public static int PlayerIdOf(IPacketSink session)
        {
            if (session is HostPeerSession peer && peer.Player != null)
            {
                return peer.Player.PublicInfo.PlayerId;
            }

            return 0;
        }
    }
}