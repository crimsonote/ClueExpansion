using ClueDetail.Core;

namespace ClueDetail.Features.Both.Handshake
{
    /// <summary>
    /// 客户端能力握手：让服务端知道"谁装了本模块"。
    /// </summary>
    /// <remarks>
    /// 不新增 opcode（那条路已被证伪，见 AGENTS.md §4.2），而是复用同一条未知字段通道：
    /// 客户端在扫描请求 <c>C_SCAN_DEVICE</c>（字段 1 = DeviceId）上附带字段 2 = 能力版本。
    /// <list type="bullet">
    /// <item>未装本模块的客户端不会写这个字段 —— 服务端读到的就是 0，天然区分，不需要往返协商；</item>
    /// <item>未装本模块的**服务端**会把该字段原样丢进自己的 _unknownFields 并忽略，不报错。</item>
    /// </list>
    /// <para>
    /// 登记结果目前只用于日志与后续提示，**不参与决策**：线索详情字段对原版客户端本来就无害，
    /// 所以下发始终是全员广播，不必按能力分包。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueHandshake",
        "客户端能力握手：装了本模块的客户端在扫描请求上附带能力版本，服务端据此登记谁支持本模块。未装的客户端没有任何字段，天然区分；仅用于统计与提示，不改变下发行为。",
        defaultEnabled: true,
        side: FeatureSide.Both)]
    internal static partial class HandshakeFeature
    {
    }
}
