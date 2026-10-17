using ClueDetail.Core;

namespace ClueDetail.Features.Host.RecordDetail
{
    /// <summary>
    /// 房主侧：让线索重新带上「详细活动」。
    /// </summary>
    /// <remarks>
    /// 0.1.18 删除了 <c>Protocol.PropositionInfo.WhatType</c>。本功能在服务端写入线索的瞬间
    /// 把设备表里的 <c>Data.DeviceData.WhatType</c> 记进旁路表，并在**任何下发路径**上
    /// 把它写回 protobuf 的未知字段区（字段号 4，0.1.18 已弃用且未复用）。
    /// <para>
    /// 对未安装本模块的客户端：未知字段会被 protobuf 原样保留并忽略，**行为与原版完全一致**，
    /// 不新增 opcode、不污染 RoomId/PlayerId/Time 等原版字段。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueRecordDetail",
        "房主侧：线索写入时记录详细活动（设备动作），并在下发时补进 protobuf 未知字段，供装了本模块的客户端还原旧版「…于{地点}{活动}」句式。未装本模块的客户端会忽略该字段，行为与原版一致。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static partial class RecordDetailFeature
    {
    }
}
