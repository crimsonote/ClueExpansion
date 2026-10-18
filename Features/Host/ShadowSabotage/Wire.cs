using Protocol;
using ServerGamePlayer = Server.Game.Player;

namespace ClueDetail.Features.Host.ShadowSabotage
{
    /// <summary>
    /// 破坏点用到的**包构造**：全部集中在这里，编排逻辑只调方法、不碰协议类型。
    /// </summary>
    internal partial class ShadowSabotageFeature
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

        // ══════════════════ P3：箭头与图钉 ══════════════════

        /// <summary>
        /// 给**所有存活者**发一支指向破坏点的断电箭头。
        /// </summary>
        /// <remarks>
        /// 照抄原版 <c>DeviceManager.BroadcastFuseArrow</c>（0.1.18 :166870）的写法：
        /// <c>BroadcastAlivePlayers(S_NOTIFY_ARROW{FuseboxArrow, Pos})</c>。
        /// <para>
        /// 刻意**不发任何文字通知** —— 只给箭头（需求口径）。
        /// </para>
        /// </remarks>
        internal static void BroadcastArrow(BreakerDevice device)
        {
            var room = Server.Game.GameRoom.Instance;
            if (device == null || device.DeviceInfo == null || device.DeviceInfo.Pos == null)
            {
                return;
            }

            if (room == null)
            {
                return;
            }

            try
            {
                room.BroadcastAlivePlayers(new S_NOTIFY_ARROW
                {
                    Type = Protocol.EArrowType.FuseboxArrow,
                    Pos = device.DeviceInfo.Pos.Clone(),
                });
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Warn($"破坏点：发送箭头失败（{ex.Message}）。");
            }
        }

        /// <summary>撤掉指向破坏点的箭头（修复时、离开 Survive 时）。</summary>
        internal static void BroadcastRemoveArrow(BreakerDevice device)
        {
            var room = Server.Game.GameRoom.Instance;
            if (device == null || device.DeviceInfo == null || device.DeviceInfo.Pos == null || room == null)
            {
                return;
            }

            try
            {
                room.BroadcastAlivePlayers(new S_REMOVE_ARROW
                {
                    Type = Protocol.EArrowType.FuseboxArrow,
                    Pos = device.DeviceInfo.Pos.Clone(),
                });
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Warn($"破坏点：撤箭头失败（{ex.Message}）。");
            }
        }

        /// <summary>
        /// 给**黑幕**加 / 撤这个破坏点的「电闸」图钉与平板条目。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 走原版 <c>GameRoom.SendSabotageMission</c>（0.1.18 :174389）—— 它**只发给
        /// <c>GameRoom.MasterMind</c>**，正是我们要的口径（其余人在地图上什么都看不到）。
        /// </para>
        /// <para>
        /// 客户端侧（:30968 / :52763）只是把 <c>deviceId</c> 当字典键与条目匹配用，
        /// 不查任何设备表 ⇒ 传我们的自定义设备 ID 完全安全。
        /// 而 <c>MissionType = ScFusebox</c> 会让它按电箱的 pin 类型与词条渲染
        /// —— 这就是「在地图上显示为电箱」。
        /// </para>
        /// </remarks>
        internal static void SendPin(BreakerState state, BreakerDevice device, bool isAdd)
        {
            var room = Server.Game.GameRoom.Instance;
            if (room == null || state == null || device == null || device.DeviceInfo == null)
            {
                return;
            }

            if (device.DeviceInfo.Pos == null)
            {
                return;
            }

            try
            {
                room.SendSabotageMission(
                    Protocol.ESchoolMission.ScFusebox,
                    state.DeviceId,
                    device.DeviceInfo.Pos,
                    isAdd);
            }
            catch (global::System.Exception ex)
            {
                global::ClueDetail.Core.Log.Warn($"破坏点：图钉{(isAdd ? "添加" : "撤除")}失败（{ex.Message}）。");
            }
        }
    }
}
