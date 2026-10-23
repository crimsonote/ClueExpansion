using ClueDetail.Core;
using Google.Protobuf;
using HarmonyLib;
using Protocol;
using GamePlayer = Server.Game.Player;
using ServerDevice = Server.Game.Device;
using ServerGameRoom = Server.Game.GameRoom;
using ServerTimeManager = Server.Game.TimeManager;

namespace ClueDetail.Features.Host.RecordDetail
{
    internal partial class RecordDetailFeature
    {
        /// <summary>
        /// 线索写入时记下"这台设备的详细活动"。
        /// </summary>
        /// <remarks>
        /// 镜像 0.1.18 <c>Server.Game/Device.cs</c> 的 <c>RecordLastUsingPlayer</c>（反编译 :166386）
        /// 的身份映射与阶段守卫：Anonymity → 0，Mastermind → 11037，Black → 66613。
        /// 时间取同一个 <c>TimeManager.Instance.SurviveTime</c> —— 新线索的 Time 就是它，
        /// 更新已有线索时也会被改成它，所以键必然对得上。
        /// </remarks>
        [HarmonyPatch(typeof(ServerDevice), nameof(ServerDevice.RecordLastUsingPlayer))]
        internal static class RecordHook
        {
            private static void Prefix(ServerDevice __instance, GamePlayer player, Define.EWhatState state)
            {
                if (!FeatureLoader.Enabled<RecordDetailFeature>()) return;
                if (__instance == null || player == null || __instance.DeviceData == null)
                {
                    return;
                }

                if (!RoomState.HostBroadcast)
                {
                    return;
                }

                if (ServerGameRoom.Instance == null || ServerGameRoom.Instance.State != EGameState.Survive)
                {
                    return;
                }

                int what = (int)__instance.DeviceData.WhatType;
                if (what == 0)
                {
                    return;
                }

                DetailStore.Remember(
                    RoleClueId(player, state),
                    ServerTimeManager.Instance.SurviveTime,
                    __instance.RoomID,
                    what);
            }

            /// <summary>
            /// 原版方法跑完之后，把活动直接补进**服务端 ClueList 里的对象**。
            /// </summary>
            /// <remarks>
            /// ⚠️ 这一步不能省（2026-10-18 实机反馈「两端都装了 mod，房主自己看到的还是不对」查出）：
            /// 客户端 <c>PacketHandler.Handle_S_ADD_CLUE</c>（:43621）第一行就是
            /// <c>if (Managers.Host == null || !Managers.Host.IsHost)</c> —— **房主自己的客户端不镜像**，
            /// 它读的是服务端 <c>ClueList</c> 里的对象。而调用点写的是
            /// <c>Info = propositionInfo.Clone()</c>（:166418）：<see cref="BroadcastHook"/> 改的是那个
            /// **克隆**，服务端对象一直没被碰过。于是出现"远端客户端有活动、房主自己反而没有"。
            /// <para>
            /// 与服务端 <c>ScanHook</c> 同一口径（它也直接改 <c>ClueList</c> 的对象）：
            /// 只加一个未知字段，原版字段一个都不动，未装本模块的一端照旧忽略它。
            /// 广播那条路仍然要保留 —— 包里的 <c>Info</c> 在这之前就已经克隆好了。
            /// </para>
            /// </remarks>
            private static void Postfix(ServerDevice __instance, GamePlayer player, Define.EWhatState state)
            {
                StampWhat(__instance, player, state);
            }

            /// <summary>
            /// 把「详细活动」写进服务端 <c>ClueList</c> 里刚写 / 刚更新的那些对象（未知字段 100）。
            /// </summary>
            /// <remarks>
            /// 抽成独立方法是因为 <c>ClueMerge</c> 的自定义合并窗口会**整替**
            /// <c>RecordLastUsingPlayer</c> —— 那时本补丁的 Postfix 不会跑，
            /// 由接管方（<c>Features/Host/ClueMerge/Patch.cs</c>）显式调用本方法补上职责。
            /// </remarks>
            internal static void StampWhat(ServerDevice __instance, GamePlayer player, Define.EWhatState state)
            {
                if (!FeatureLoader.Enabled<RecordDetailFeature>()) return;
                if (__instance == null || player == null || !RoomState.HostBroadcast)
                {
                    return;
                }

                int what = DetailStore.Recall(
                    RoleClueId(player, state),
                    ServerTimeManager.Instance.SurviveTime,
                    __instance.RoomID);
                if (what == 0)
                {
                    return;
                }

                var list = __instance.ClueList;
                if (list == null)
                {
                    return;
                }

                for (int i = 0; i < list.Count; i++)
                {
                    var info = list[i];
                    if (info != null && !info.Destroyed &&
                        !Define.IsDestroyEvidenceClue(info) &&
                        info.PlayerId == RoleClueId(player, state) &&
                        info.RoomId == __instance.RoomID)
                    {
                        UnknownFields.SetWhat(info, what);
                    }
                }
            }

            /// <summary>与 <c>Device.RecordLastUsingPlayer</c>（:166393）的身份映射保持一致。</summary>
            private static int RoleClueId(GamePlayer player, Define.EWhatState state)
            {
                switch (state)
                {
                    case Define.EWhatState.Anonymity:
                        return 0;
                    case Define.EWhatState.Mastermind:
                        return 11037;
                    case Define.EWhatState.Black:
                        return 66613;
                    default:
                        return player.PublicInfo.PlayerId;
                }
            }
        }

        /// <summary>
        /// 扫描下发前，把设备既有线索的详细活动写进它们的未知字段。
        /// </summary>
        /// <remarks>
        /// <c>S_SCAN_DEVICE</c> 会把 <c>ClueList</c> 里的对象引用直接 <c>AddRange</c> 进包里，
        /// 序列化发生在方法体内部 —— 所以 Prefix 改的是同一批对象，改得动。
        /// <c>Device.Scan</c> 本身只有三行，这里不改它的行为，只补数据。
        /// </remarks>
        [HarmonyPatch(typeof(ServerDevice), nameof(ServerDevice.Scan))]
        internal static class ScanHook
        {
            private static void Prefix(ServerDevice __instance)
            {
                if (!FeatureLoader.Enabled<RecordDetailFeature>()) return;
                if (__instance == null || !RoomState.HostBroadcast)
                {
                    return;
                }

                var data = __instance.DeviceData;
                var list = __instance.ClueList;
                if (data == null || list == null)
                {
                    return;
                }

                int what = (int)data.WhatType;
                if (what == 0)
                {
                    return;
                }

                for (int i = 0; i < list.Count; i++)
                {
                    var info = list[i];

                    // ⚠️ 「销毁证据」线索（PlayerId ∈ {11038, 66613}）不写：它们说的是"这台设备的证据
                    // 被销毁了"这个**动作**，原版渲染成「销毁了证据」；写进设备平时的活动就变成错的
                    // （实机反馈过：黑那一行显示成「点燃了蜡烛」）。判据与 ClueWhat.TryResolve 的 ⓪ 段一致。
                    if (info != null && !info.Destroyed && !Define.IsDestroyEvidenceClue(info) &&
                        UnknownFields.GetWhat(info) == 0)
                    {
                        UnknownFields.SetWhat(info, what);
                    }
                }
            }
        }

        /// <summary>
        /// 实时广播（S_ADD_CLUE）前，把详细活动写进要发出去的 <c>Info</c>。
        /// </summary>
        /// <remarks>
        /// 原版在 <c>RecordLastUsingPlayer</c> 里**先 <c>Clone()</c> 再 <c>Broadcast</c>**，
        /// 而我们在那边只能挂 Prefix（那时对象还没创建）—— 所以事后改 <c>ClueList</c> 里的对象
        /// 已经来不及，必须在这里补。<c>Broadcast</c> → <c>Replicator.All</c> 的序列化
        /// 发生在本 Prefix 之后，改得动。
        /// </remarks>
        [HarmonyPatch(typeof(ServerGameRoom), "Broadcast", new[] { typeof(IMessage), typeof(int) })]
        internal static class BroadcastHook
        {
            private static void Prefix(IMessage packet)
            {
                if (!FeatureLoader.Enabled<RecordDetailFeature>()) return;
                if (!(packet is S_ADD_CLUE add) || add.Info == null || !RoomState.HostBroadcast)
                {
                    return;
                }

                int what = DetailStore.Recall(add.Info.PlayerId, add.Info.Time, add.Info.RoomId);
                Diagnostics.TraceHost(add.Info.PlayerId, add.Info.Time, add.Info.RoomId, what, what != 0);
                if (what != 0)
                {
                    UnknownFields.SetWhat(add.Info, what);
                }
            }
        }
    }
}