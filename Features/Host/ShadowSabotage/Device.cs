using Protocol;
using ServerDevice = Server.Game.Device;
using ServerGamePlayer = Server.Game.Player;

namespace ClueDetail.Features.Host.ShadowSabotage
{
    /// <summary>
    /// 一个伪装成小熊的服务端破坏点。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不直接 <c>new Server.Game.Summon(...)</c>：</b>
    /// </para>
    /// <list type="bullet">
    /// <item><c>Summon</c> 的构造函数要求 <c>Player owner</c>（0.1.18 :166135），我们没有主人；</item>
    /// <item>原版把召唤物放进 <c>DeviceManager._summons</c> 只走 <c>CreateSummon</c>（:167300），
    /// 而一旦进去就会被凛的附身技能（<c>SkillComponent.PossessMarionette</c> → <c>GetSummon</c>）
    /// 以及其它模块的「找召唤物」逻辑认领 —— 我们走 <c>SpawnDevice</c> 就不会进那个列表。</item>
    /// </list>
    /// <para>
    /// <b>外观从哪来：</b>客户端 <c>Managers.Device.HandleAddDevice</c>（:28247）先查
    /// <c>Managers.Data.DeviceDic</c>；我们的 ID 不在那张表里，于是退回
    /// <c>Spawn(info.Type.ToString(), info)</c> ⇒ prefab 名 = <c>"Summon"</c> ⇒ 小熊。
    /// </para>
    /// <para>
    /// <b>StateList 必须正好 2 槽</b>（客户端 <c>Summon.RefreshState</c> 的断言，:4139）：
    /// <c>[0]</c> = 0 正常 / 2 已破坏（刻意避开 1，因为 1 会触发范围圈与麦克风静音），
    /// <c>[1]</c> = 「假主人」位（对谁发假 owner，谁就在自己的客户端上看到按 E 提示）。
    /// </para>
    /// </remarks>
    internal sealed class BreakerDevice : ServerDevice
    {
        /// <summary>这个破坏点是哪一种（身份识别 / 时钟记录）。</summary>
        public BreakerKind Kind { get; private set; }

        public BreakerDevice(BreakerKind kind, int deviceId, float posX, float posY)
        {
            Kind = kind;

            var data = new Data.DeviceData
            {
                DataId = deviceId,
                DeviceType = EDeviceType.Summon,
                Pos = new PosInfo { X = posX, Y = posY },

                // WhatType 保持 WNone：这个设备**不参与**原版线索系统，
                // 它的线索是我们自己在破坏完成时注入的（规划 §12.4）。
            };

            // 基类实现会设好 DeviceInfo{DeviceId,Type,Pos,Bubble=0}、ID、RoomID，
            // 并且把 StateList 清成只有 [0] 一槽。
            SetInfo(data);

            DeviceInfo.StateList.Clear();
            DeviceInfo.StateList.Add(0);
            DeviceInfo.StateList.Add(0);
        }

        /// <summary>
        /// 玩家在这个破坏点上按 E（客户端发 <c>C_INTERACT_SUMMON</c>）。
        /// </summary>
        /// <remarks>
        /// 链路：<c>Handle_C_INTERACT_SUMMON</c>（0.1.18 :177619）→
        /// <c>GameRoom.Push(DeviceManager.Interact, player, SummonId, packet)</c> →
        /// <c>DeviceManager.Interact</c>（:166617，会挡掉非 Survive 阶段）→ 到我们这里。
        /// 所以**不需要拦任何客户端包**。
        /// </remarks>
        public override void Interact(ServerGamePlayer player, global::Packet pkt)
        {
            ShadowSabotageFeature.OnInteract(this, player);
        }
    }
}
