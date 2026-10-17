using System.Collections.Generic;
using ClueDetail.Core.Net;
using DummyClient;

namespace ClueDetail.Core
{
    /// <summary>
    /// 客户端能力登记（服务端侧）：哪些玩家装了本模块。
    /// </summary>
    /// <remarks>
    /// 走自定义协议 <c>CdHello</c>：客户端在**第一次发包时**顺带声明（挂在
    /// <c>HostPeerSession.Send</c> 的 Prefix 上，那是所有出站包的必经之路），服务端收到即登记。
    /// 未装本模块的客户端不会发这条消息 —— 天然区分，不需要往返协商。
    /// <para>
    /// 比早前"寄生在原版包未知字段上"干净的地方：**时机由我们决定**，
    /// 不必再为了赶在 <c>HostPeerSession.Player</c> 绑定之前，把标记冗余到四五个原版包上。
    /// </para>
    /// <para>
    /// 登记结果目前只用于日志与自检，不参与决策：详情对原版客户端本来就无害，下发始终是全员广播。
    /// </para>
    /// </remarks>
    internal static class ClientCapability
    {
        /// <summary>能力版本，上报内容变化就 +1。</summary>
        public const int Version = 1;

        private static readonly HashSet<int> Supporters = new HashSet<int>();
        private static bool _helloSent;

        public static void Mark(int playerId, int version)
        {
            if (playerId != 0 && Supporters.Add(playerId))
            {
                Log.Info($"客户端能力上报：玩家 {playerId} 装有本模块（v{version}，累计 {Supporters.Count} 人）。");
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
            _helloSent = false;
        }

        /// <summary>客户端侧：确保本次连接已声明过能力（每次只发一次）。</summary>
        public static void EnsureHello(IPacketSink session)
        {
            if (_helloSent || session == null)
            {
                return;
            }

            _helloSent = true;
            CdWire.Send(session, new CdHello { Version = Version });
        }

        /// <summary>回到大厅 / 换局时重置，下次连接会重新声明。</summary>
        public static void ResetHello()
        {
            _helloSent = false;
        }
    }
}
