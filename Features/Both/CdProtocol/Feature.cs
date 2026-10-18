using ClueDetail.Core;

namespace ClueDetail.Features.Both.CdProtocol
{
    /// <summary>
    /// 自定义协议（ClueDetail 自有 opcode 段）的收发装配。
    /// </summary>
    /// <remarks>
    /// 本模块**不再往原版 protobuf 消息里塞未知字段** —— 那条路要反射
    /// <c>Google.Protobuf</c> 的私有实现（<c>_unknownFields</c>/<c>fields</c>/<c>varintList</c>），
    /// 换版本即断，而且必须寄生在某个原版包上、时机被宿主包绑死。
    /// 现在改成真正的自定义 opcode：
    /// <list type="bullet">
    /// <item>发送：拦 <c>SteamP2PSession.Send(IMessage)</c> / <c>ServerSession.Send(IMessage)</c>，
    /// 只对自家消息改道走 public 的裸字节出口（<c>SendBytes</c> / <c>Session.Send(ArraySegment&lt;byte&gt;)</c>）；</item>
    /// <item>接收：<c>PacketManager.RegisterMake/RegisterHandler</c>（都是 public）注册自家 opcode。</item>
    /// </list>
    /// <para>
    /// 对原版的影响面为零：原版消息原样走原路径；opcode 取 61000 起的高位段；
    /// 注册前检查占用，撞车就放弃而不是覆盖；未装本模块的一端收到这些包会静默丢弃。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueProtocol",
        "自定义协议：用本模块自有的 opcode 段传递能力声明、房主开关状态与线索详情。原版消息一律走原路径；opcode 取高位段且注册前查占用，撞车不覆盖；未装本模块的一端会静默丢弃这些包。",
        defaultEnabled: true,
        side: FeatureSide.Both)]
    internal partial class CdProtocolFeature
    {
        /// <summary>注册收发（由 FeatureLoader 在挂补丁前调用）。</summary>
        public static void OnLoaded()
        {
            ClueDetail.Core.Net.CdWire.RegisterAll();
        }

        /// <summary>
        /// 热开启：让本机的客户端**重新声明一次**能力。
        /// </summary>
        /// <remarks>
        /// 必要的原因：<c>CdHello</c> 是「每次连接只发一次」的（<c>ClientCapability.EnsureHello</c>）。
        /// 关掉本功能的这段时间里，那个标记还留着 ⇒ 重开后不会补发 ⇒ 服务端就不知道谁装了模块。
        /// </remarks>
        public static void OnEnabled()
        {
            ClientCapability.ResetHello();
        }

        /// <summary>热关闭：同样清掉「已声明」标记，重开后会重新声明。</summary>
        public static void OnDisabled()
        {
            ClientCapability.ResetHello();
        }
    }
}
