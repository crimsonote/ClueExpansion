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

        /// <summary>
        /// 解密钩子：出站改写把某条「某人」线索按收件人改成实名后，如果同一
        /// (房间, 时刻) 已经没有别的未解密「某人」，就用 <c>S_REMOVE_CLUE</c> 删掉旧的匿名条目。
        /// </summary>
        /// <remarks>
        /// 由 <c>ShadowSabotage.Wire.RewriteOutflow</c> 在改写 <c>S_ADD_CLUE</c> /
        /// <c>S_ADD_MY_CLUES</c> 时调用。开关关闭时什么都不做（两条都保留）。
        /// 纯服务端：<c>S_REMOVE_CLUE</c> 是原版包，客户端原生处理。
        /// </remarks>
        internal static void OnDecrypted(int recipient, PropositionInfo anonymous, PropositionInfo real)
        {
            if (!FeatureLoader.Enabled<ClueShareFeature>() || !DedupValue)
            {
                return;
            }

            if (anonymous == null || real == null || real.PlayerId == 0 || anonymous.PlayerId != 0)
            {
                return;
            }

            if (HasOtherAnonymous(anonymous))
            {
                return;     // 还有未解密的「某人」⇒ 都保留
            }

            int deviceId = FindDeviceOf(anonymous);
            if (deviceId == 0)
            {
                return;
            }

            SendRemove(recipient, deviceId, anonymous);
        }

        private static bool HasOtherAnonymous(PropositionInfo self)
        {
            ServerDeviceManager dm = ServerDeviceManager.Instance;
            if (dm == null || dm.Objects == null)
            {
                return false;
            }

            for (int i = 0; i < dm.Objects.Count; i++)
            {
                ServerDevice device = dm.Objects[i];
                if (device == null || device.ClueList == null)
                {
                    continue;
                }

                for (int j = 0; j < device.ClueList.Count; j++)
                {
                    PropositionInfo clue = device.ClueList[j];
                    if (clue == null || ReferenceEquals(clue, self))
                    {
                        continue;
                    }

                    if (clue.RoomId == self.RoomId
                        && clue.Time == self.Time
                        && clue.PlayerId == 0
                        && UnknownFields.GetRealOwner(clue) != 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static int FindDeviceOf(PropositionInfo info)
        {
            ServerDeviceManager dm = ServerDeviceManager.Instance;
            if (dm == null || dm.Objects == null)
            {
                return 0;
            }

            for (int i = 0; i < dm.Objects.Count; i++)
            {
                ServerDevice device = dm.Objects[i];
                if (device == null || device.ClueList == null)
                {
                    continue;
                }

                for (int j = 0; j < device.ClueList.Count; j++)
                {
                    if (ReferenceEquals(device.ClueList[j], info))
                    {
                        return device.ID;
                    }
                }
            }

            return 0;
        }

        private static void SendRemove(int recipient, int deviceId, PropositionInfo anonymous)
        {
            ServerGameRoom room = ServerGameRoom.Instance;
            if (room == null || room.Players == null)
            {
                return;
            }

            for (int i = 0; i < room.Players.Count; i++)
            {
                ServerGamePlayer p = room.Players[i];
                if (p != null && p.PublicInfo != null && p.PublicInfo.PlayerId == recipient)
                {
                    p.Session?.Send(new S_REMOVE_CLUE
                    {
                        DeviceId = deviceId,
                        Info = anonymous.Clone(),
                    });
                    global::ClueDetail.Core.Log.Info(
                        $"线索互通：解密后剔除旧匿名条目（收件人 {recipient}，设备 {deviceId}，t={anonymous.Time}）。");
                    return;
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
