using BepInEx.Configuration;
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
    /// <para>
    /// 同时每条线索都带上「房主侧状态」（字段号 6）：装了本模块的客户端据此区分
    /// "房主没装本模块"（无标记 → 允许自采集兜底）与"房主装了但关闭"（标记 0 → 客户端跟随关闭）。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueRecordDetail",
        "房主侧：线索写入时记录详细活动（设备动作），并在下发时补进 protobuf 未知字段，供装了本模块的客户端还原旧版「…于{地点}{活动}」句式；同时下发本开关状态，让客户端跟随。未装本模块的客户端会忽略这些字段，行为与原版一致。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal static partial class RecordDetailFeature
    {
        /// <summary>房间级开关：是否下发详细活动。关闭后装了本模块的客户端也会跟着不显示。</summary>
        public static ConfigEntry<bool> BroadcastDetail { get; private set; }

        public static void Bind(ConfigFile config, string section)
        {
            BroadcastDetail = config.Bind(
                section,
                "BroadcastDetail",
                true,
                "是否向所有客户端下发线索的详细活动（设备动作）。\n" +
                "关闭后：装了本模块的客户端也会跟着不显示详细活动，且不会再自行记录——\n" +
                "这是「房主主动关闭」与「房主根本没装本模块」的区别所在。");

            RoomState.HostBroadcast = BroadcastDetail.Value;
            BroadcastDetail.SettingChanged += (_, __) => RoomState.HostBroadcast = BroadcastDetail.Value;
        }
    }
}
