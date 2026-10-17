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
    internal static partial class RecordDetailFeature
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

                int playerId = player.PublicInfo.PlayerId;
                switch (state)
                {
                    case Define.EWhatState.Anonymity:
                        playerId = 0;
                        break;
                    case Define.EWhatState.Mastermind:
                        playerId = 11037;
                        break;
                    case Define.EWhatState.Black:
                        playerId = 66613;
                        break;
                }

                DetailStore.Remember(playerId, ServerTimeManager.Instance.SurviveTime, __instance.RoomID, what);
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
                    if (info != null && !info.Destroyed && UnknownFields.GetWhat(info) == 0)
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
                if (!(packet is S_ADD_CLUE add) || add.Info == null || !RoomState.HostBroadcast)
                {
                    return;
                }

                int what = DetailStore.Recall(add.Info.PlayerId, add.Info.Time, add.Info.RoomId);
                if (what != 0)
                {
                    UnknownFields.SetWhat(add.Info, what);
                }
            }
        }
    }
}