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
        /// 线索写入前记下"这台设备的详细活动"。
        /// </summary>
        /// <remarks>
        /// 镜像 0.1.18 <c>Server.Game/Device.cs</c> 的 <c>RecordLastUsingPlayer</c>（反编译 :166386）
        /// 的身份映射：Anonymity → 0，Mastermind → 11037，Black → 66613。
        /// 时间取与该方法同样的 <c>TimeManager.Instance.SurviveTime</c> —— 新线索的 Time 就是它，
        /// 更新已有线索时也会被改成它，所以键必然对得上。
        /// </remarks>
        [HarmonyPatch(typeof(ServerDevice), nameof(ServerDevice.RecordLastUsingPlayer))]
        internal static class RecordHook
        {
            private static void Prefix(ServerDevice __instance, GamePlayer player, Define.EWhatState state)
            {
                if (__instance == null || player == null)
                {
                    return;
                }

                var data = __instance.DeviceData;
                if (data == null)
                {
                    return;
                }

                int what = (int)data.WhatType;
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

                DetailStore.Remember(__instance.ID, playerId, ServerTimeManager.Instance.SurviveTime, what);
            }
        }

        /// <summary>
        /// 扫描下发前，把设备既有的线索补齐详细活动与房主状态。
        /// </summary>
        /// <remarks>
        /// <c>S_SCAN_DEVICE</c> 只有 InfoList、不带 DeviceId，客户端无法自己反查设备；
        /// 而这里就是服务端最后能确定 deviceId 的位置。方法体本身只有三行，Prefix 改的是
        /// <c>ClueList</c> 里的对象引用，随后 <c>AddRange</c> 取到的正是同一批对象。
        /// <para>
        /// 这也是客户端**第一次必然后收到**状态标记的地方：线索内容主要来自扫描，
        /// 所以渲染发生时状态一定已经就绪。
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(ServerDevice), nameof(ServerDevice.Scan))]
        internal static class ScanHook
        {
            private static void Prefix(ServerDevice __instance)
            {
                if (__instance == null)
                {
                    return;
                }

                var list = __instance.ClueList;
                if (list == null)
                {
                    return;
                }

                bool enabled = RoomState.HostBroadcast;
                int what = __instance.DeviceData == null ? 0 : (int)__instance.DeviceData.WhatType;

                for (int i = 0; i < list.Count; i++)
                {
                    var info = list[i];
                    if (info == null || info.Destroyed)
                    {
                        continue;
                    }

                    UnknownFields.SetFlag(info, enabled);

                    if (enabled && what != 0 && UnknownFields.GetWhat(info) == 0)
                    {
                        UnknownFields.SetWhat(info, what);
                    }
                }
            }
        }

        /// <summary>
        /// 实时广播（S_ADD_CLUE）时补详细活动与房主状态。
        /// </summary>
        /// <remarks>
        /// 原版在 <c>RecordLastUsingPlayer</c> 里先 <c>Clone()</c> 再 <c>Broadcast</c>，
        /// 所以事后修改 ClueList 里的对象已经来不及。这里挂在广播入口上：
        /// <c>Broadcast</c> → <c>Replicator.All</c> 序列化发生在我们的 Prefix 之后，改得动。
        /// <para>关掉 <c>BroadcastDetail</c> 时仍然写状态标记（值 0），客户端据此跟随关闭。</para>
        /// </remarks>
        [HarmonyPatch(typeof(ServerGameRoom), "Broadcast", new[] { typeof(IMessage), typeof(int) })]
        internal static class BroadcastHook
        {
            private static void Prefix(IMessage packet)
            {
                if (!(packet is S_ADD_CLUE add) || add.Info == null)
                {
                    return;
                }

                bool enabled = RoomState.HostBroadcast;
                UnknownFields.SetFlag(add.Info, enabled);

                if (!enabled)
                {
                    return;
                }

                int what = DetailStore.Recall(add.DeviceId, add.Info.PlayerId, add.Info.Time);
                if (what != 0)
                {
                    UnknownFields.SetWhat(add.Info, what);
                }
            }
        }
    }
}
