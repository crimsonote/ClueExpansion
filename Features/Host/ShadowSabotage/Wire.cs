using Protocol;
using ServerGamePlayer = Server.Game.Player;

namespace ClueDetail.Features.Host.ShadowSabotage
{
    /// <summary>
    /// 破坏点用到的**包构造**：全部集中在这里，编排逻辑只调方法、不碰协议类型。
    /// </summary>
    internal static partial class ShadowSabotageFeature
    {
        /// <summary>
        /// 把破坏点伪装成「这个玩家自己的小熊」—— 也就是把 <c>StateList[1]</c> 单发成他的 PlayerId。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 为什么能成：客户端 <c>Summon.GetInteractMessage</c>（0.1.18 :4159）只认
        /// <c>StateList[1] == Managers.Player.MyPlayer.PublicInfo.PlayerId</c>，
        /// 成立就返回词条 <c>SummonInteract</c>（按 E 提示），按下去发 <c>C_INTERACT_SUMMON</c>。
        /// 服务端那条链路（<c>DeviceManager.Interact</c> → <c>Device.Interact</c>）只看设备 ID，
        /// **不校验主人**，所以假主人是纯客户端观感、服务端完全可控。
        /// </para>
        /// <para>
        /// 为什么必须**单发**而不是改 <c>DeviceInfo.StateList[1]</c> 后 <c>BroadcastState()</c>：
        /// 那是全场广播，会让所有人都变成「主人」（人人屏幕上一只属于自己的熊）。
        /// </para>
        /// <para>
        /// ⚠ 已接受的副作用：客户端 <c>Summon.Update</c> 每 0.2 秒做一次近身检测
        /// （<c>RuneSfx</c> + HUD 描边，:4027），判据就是 <c>IsOwner</c> —— 假主人无法规避它。
        /// 所以只在**近距离**才发，把窗口压到「他人就在旁边」。
        /// </para>
        /// </remarks>
        internal static void SendFakeOwner(BreakerDevice device, ServerGamePlayer player)
        {
            if (player == null || player.PublicInfo == null)
            {
                return;
            }

            SendOwnerSlot(device, player, player.PublicInfo.PlayerId);
        }

        /// <summary>把「假主人」收回：把 <c>StateList[1]</c> 单发成 0（= 没有主人）。</summary>
        /// <remarks>
        /// 0 是安全值：客户端 <c>IsOwner</c> 要求 <c>StateList[1] == MyPlayerID</c>，
        /// 而玩家 id 从 1 起（<c>PlayerId</c> 是座位号），0 永不相等。
        /// </remarks>
        internal static void SendTruthOwner(BreakerDevice device, ServerGamePlayer player)
        {
            SendOwnerSlot(device, player, 0);
        }

        private static void SendOwnerSlot(BreakerDevice device, ServerGamePlayer player, int ownerValue)
        {
            if (device == null || player == null || player.Session == null)
            {
                return;
            }

            try
            {
                // 克隆：绝不能把临时的假主人写回服务端权威的 DeviceInfo ——
                // 那会被下一次 BroadcastState() 带出去（而且可能被别人读到）。
                var info = device.DeviceInfo.Clone();
                if (info.StateList.Count < 2)
                {
                    return;
                }

                info.StateList[1] = ownerValue;
                player.Session.Send(new S_MODIFY_DEVICE { Info = info });
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Warn($"破坏点：伪装主人下发失败（{ex.Message}）。");
            }
        }

        /// <summary>
        /// 把破坏点的**真状态**（<c>StateList[0]</c>）广播给全场。
        /// </summary>
        /// <remarks>
        /// ⚠ 广播的是服务端权威的 <c>DeviceInfo</c>，其中 <c>StateList[1]</c> 恒为 0
        /// ⇒ **会把各客户端上的假主人一起冲掉**。所以调用方之后必须让伪装名单失效、立刻重发
        /// （<see cref="RefreshFakes"/> 做这件事）。
        /// </remarks>
        internal static void BroadcastState(BreakerDevice device)
        {
            if (device == null)
            {
                return;
            }

            try
            {
                device.BroadcastState();
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Warn($"破坏点：状态广播失败（{ex.Message}）。");
            }
        }
    }
}
