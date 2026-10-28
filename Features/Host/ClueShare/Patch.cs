using System;
using ClueDetail.Core;
using HarmonyLib;
using Protocol;
using PbMessage = Google.Protobuf.IMessage;
using ServerGamePlayer = Server.Game.Player;
using ServerGameRoom = Server.Game.GameRoom;
using ServerDevice = Server.Game.Device;
using ServerDeviceManager = Server.Game.DeviceManager;

namespace ClueDetail.Features.Host.ClueShare
{
    internal partial class ClueShareFeature
    {
        /// <summary>
        /// 调查阶段：把黑 / 黑幕各自的线索互发一份。
        /// </summary>
        /// <remarks>
        /// 原版 <c>StartDetective</c> 只给每人发 <c>PlayerId == 自己</c> 的线索；
        /// 这里在它之后补一个 <c>S_ADD_MY_CLUES</c>，内容 = 队友的线索。
        /// 冲突（同一时刻同一房间）由客户端按 <c>AlreadyExistClue</c> 同口径合并，
        /// 服务端不删任何真值。
        /// </remarks>
        [HarmonyPatch(typeof(ServerGameRoom), "StartDetective")]
        internal static class DetectiveShareHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!FeatureLoader.Enabled<ClueShareFeature>()) return;

                try
                {
                    ShareAtDetective();
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "线索互通：调查阶段同步失败。");
                }
            }
        }

        /// <summary>
        /// 二级：黑 / 黑幕的线索广播（<c>S_ADD_CLUE</c>）时，额外单发给队友。
        /// </summary>
        /// <remarks>
        /// 排除尸体（<c>EClueType.Corpse</c>）与刀架（<c>WArmory</c>），避免平板提示暴露身份。
        /// </remarks>
        [HarmonyPatch(typeof(ServerGameRoom), "Broadcast", new[] { typeof(PbMessage), typeof(int) })]
        internal static class ClueBroadcastShareHook
        {
            private static void Prefix(PbMessage packet)
            {
                if (!FeatureLoader.Enabled<ClueShareFeature>()) return;
                if (TierValue < 2) return;

                try
                {
                    ShareOne(packet);
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "线索互通：广播同步失败。");
                }
            }
        }

        private static void ShareAtDetective()
        {
            ServerGameRoom room = ServerGameRoom.Instance;
            if (room == null || room.Players == null)
            {
                return;
            }

            ServerGamePlayer black = null;
            for (int i = 0; i < room.Players.Count; i++)
            {
                ServerGamePlayer p = room.Players[i];
                if (p != null && p.PublicInfo != null && p.Color == EPlayerColor.Dark)
                {
                    black = p;
                    break;
                }
            }

            ServerGamePlayer mastermind = room.MasterMind;
            if (black == null || mastermind == null || black == mastermind)
            {
                return;
            }

            SendPartnerClues(black, mastermind.PublicInfo.PlayerId);
            SendPartnerClues(mastermind, black.PublicInfo.PlayerId);
        }

        private static void SendPartnerClues(ServerGamePlayer receiver, int partnerId)
        {
            if (receiver == null || receiver.Session == null || partnerId == 0)
            {
                return;
            }

            var message = new S_ADD_MY_CLUES();
            ServerDeviceManager dm = ServerDeviceManager.Instance;
            if (dm == null || dm.Objects == null)
            {
                return;
            }

            for (int i = 0; i < dm.Objects.Count; i++)
            {
                ServerDevice device = dm.Objects[i];
                if (device == null || device.ClueList == null || IsExcluded(device))
                {
                    continue;
                }

                for (int j = 0; j < device.ClueList.Count; j++)
                {
                    PropositionInfo clue = device.ClueList[j];
                    if (clue != null && !clue.Destroyed && clue.PlayerId == partnerId)
                    {
                        message.InfoList.Add(clue);
                    }
                }
            }

            if (message.InfoList.Count == 0)
            {
                return;
            }

            receiver.Session.Send(message);
            global::ClueDetail.Core.Log.Info(
                $"线索互通：调查阶段向 {receiver.Name} 补发队友线索 {message.InfoList.Count} 条。");
        }

        /// <summary>二级：单条线索广播时转给队友。</summary>
        private static void ShareOne(PbMessage packet)
        {
            if (!(packet is S_ADD_CLUE add) || add.Info == null)
            {
                return;
            }

            int owner = add.Info.PlayerId;
            ServerGameRoom room = ServerGameRoom.Instance;
            if (room == null || room.Players == null)
            {
                return;
            }

            if (IsExcludedDevice(add.DeviceId))
            {
                return;
            }

            ServerGamePlayer black = null;
            for (int i = 0; i < room.Players.Count; i++)
            {
                ServerGamePlayer p = room.Players[i];
                if (p != null && p.PublicInfo != null && p.Color == EPlayerColor.Dark)
                {
                    black = p;
                    break;
                }
            }

            ServerGamePlayer mastermind = room.MasterMind;
            if (black == null || mastermind == null)
            {
                return;
            }

            // 只转发黑 / 黑幕自己产生的那条；其他玩家（含共享出去的）不参与。
            int blackId = black.PublicInfo != null ? black.PublicInfo.PlayerId : 0;
            int mmId = mastermind.PublicInfo != null ? mastermind.PublicInfo.PlayerId : 0;

            if (owner == blackId)
            {
                mastermind.Session?.Send(add);
            }
            else if (owner == mmId)
            {
                black.Session?.Send(add);
            }
        }

        private static bool IsExcluded(ServerDevice device)
        {
            // 尸体线索：DeviceType 是尸体；刀架：WhatType == WArmory。
            if (device.DeviceType == EDeviceType.Corpse)
            {
                return true;
            }

            return device.DeviceData != null && device.DeviceData.WhatType == EWhatType.WArmory;
        }

        private static bool IsExcludedDevice(int deviceId)
        {
            ServerDeviceManager dm = ServerDeviceManager.Instance;
            if (dm == null || dm.Objects == null)
            {
                return true;
            }

            for (int i = 0; i < dm.Objects.Count; i++)
            {
                ServerDevice device = dm.Objects[i];
                if (device != null && device.ID == deviceId)
                {
                    return IsExcluded(device);
                }
            }

            return false;
        }
    }
}
