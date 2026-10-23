﻿using BepInEx.Configuration;
using ClueDetail.Core;
using ClueDetail.Core.Net;

namespace ClueDetail.Features.Host.RecordDetail
{
    /// <summary>
    /// 房主侧：让线索重新带上「详细活动」。
    /// </summary>
    /// <remarks>
    /// 0.1.18 删除了 <c>Protocol.PropositionInfo.WhatType</c>。本功能在服务端写入线索的瞬间
    /// 把设备表里的 <c>Data.DeviceData.WhatType</c> 记进旁路表，并通过**本模块自己的协议**
    /// （<c>CdClueDetail</c>）主动下发给全房。
    /// <para>
    /// 对未安装本模块的客户端：这些自定义 opcode 它不认识，
    /// <c>PacketManager.OnRecvPacket</c> 查不到映射会静默丢弃（0.1.18 :44894），
    /// **不报错、不改变任何原版行为**。
    /// </para>
    /// <para>
    /// 同时把「房主侧开关」经 <c>CdState</c> 下发给装了本模块的客户端：
    /// 客户端据此区分"房主没装本模块"（收不到状态 → 允许自采集兜底）
    /// 与"房主装了但关闭"（收到 Enabled=0 → 客户端跟随关闭，连自采集一起停）。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueRecordDetail",
        "房主侧：线索写入时记录详细活动（设备动作），并经本模块自有协议下发给全房，供装了本模块的客户端还原旧版「…于{地点}{活动}」句式；同时下发本开关状态让客户端跟随。未装本模块的客户端会静默忽略这些包，行为与原版一致。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal partial class RecordDetailFeature
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

            // ⚠️ 2026-10-19 加：这一项一旦是 false，**全房（含房主自己）都不显示活动**，
            // 而 .cfg 里的值会跨版本保留（默认值后来改成 true 也不会覆盖已存在的键）⇒
            // 实机里因此出现过"活动与设备都不见了"的排查。启动时明写一行，值不再隐形。
            global::ClueDetail.Core.Log.Info(
                "线索详细活动下发：" +
                (BroadcastDetail.Value
                    ? "开（装本模块的客户端可还原「…于{地点}{活动}」）"
                    : "关 ★ 全房都不显示活动，连房主自己也一样；要恢复请在 CONFIG 页「线索拓展」标签里拨开「线索详细活动」，或把 .cfg 里 [ClueRecordDetail] BroadcastDetail 改成 true（改文件后需重载配置/重启，页面开关是热生效）。"));

            BroadcastDetail.SettingChanged += (_, __) =>
            {
                RoomState.HostBroadcast = BroadcastDetail.Value;
                // 自定义协议的好处：可以立刻通知，不必等下一次线索包。
                CdWire.BroadcastState();
            };
        }

        /// <summary>
        /// 热开启：恢复房主侧的下发状态，并**立刻**告知全房（客户端据此恢复显示详细活动）。
        /// </summary>
        public static void OnEnabled()
        {
            RoomState.HostBroadcast = BroadcastDetail != null && BroadcastDetail.Value;
            NotifyClients();
        }

        /// <summary>
        /// 热关闭：把「房主已关闭」的状态广播出去。
        /// </summary>
        /// <remarks>
        /// 不做这一步的话：功能关了但客户端还等着我们的详情 —— 它们会一直显示「装载中」的空档。
        /// </remarks>
        public static void OnDisabled()
        {
            RoomState.HostBroadcast = false;
            NotifyClients();
        }

        private static void NotifyClients()
        {
            // ⚠️ 2026-10-19 实机日志里出现过「详细活动：状态广播失败（NRE）」：
            // 热开启时常常还没进房 ⇒ 根本没有可发的对象，那不是故障。
            // 不先判一下的话，日志会把"没人可发"误导成"广播坏了"。
            if (global::Server.Game.GameRoom.Instance == null)
            {
                return;
            }

            try
            {
                CdWire.BroadcastState();
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Warn($"详细活动：状态广播失败（{ex.Message}）。");
            }
        }
    }
}