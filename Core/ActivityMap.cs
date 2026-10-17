using System;
using System.Collections.Generic;
using Protocol;
using UnityEngine;

namespace ClueDetail.Core
{
    /// <summary>
    /// 「线索 → 设备」关联表（客户端侧自采集）。
    /// </summary>
    /// <remarks>
    /// 服务端未装本模块、或该条线索是扫描得来的（<c>S_SCAN_DEVICE</c> 只带 InfoList、不带 DeviceId）时，
    /// 客户端手里唯一能还原「这台设备的详细活动」的途径就是自己记住"刚才扫的是哪台设备"。
    /// <para>
    /// 关联方式：<c>DeviceBase.Scan</c> 发起时记下待扫设备 id，<c>Handle_S_SCAN_DEVICE</c> 到达时
    /// 把这一批线索全部登记到该设备上。键用 (PlayerId, Time, RoomId) —— 与 <c>ClueManager.AlreadyExistClue</c>
    /// 的原版去重口径一致。
    /// </para>
    /// <para>纯内存、随场景清空；只读游戏数据，不参与任何发包。</para>
    /// </remarks>
    internal static class ActivityMap
    {
        private static readonly Dictionary<string, int> ByClue = new Dictionary<string, int>();

        /// <summary>刚发起扫描、还没收到 S_SCAN_DEVICE 的设备 id（0 = 无）。</summary>
        public static int PendingDeviceId { get; set; }

        public static void Clear()
        {
            ByClue.Clear();
            PendingDeviceId = 0;
        }

        public static string Key(int playerId, int time, int roomId)
        {
            return playerId + ":" + time + ":" + roomId;
        }

        public static void Remember(int playerId, int time, int roomId, int deviceId)
        {
            if (deviceId == 0)
            {
                return;
            }

            ByClue[Key(playerId, time, roomId)] = deviceId;
        }

        public static int DeviceOf(int playerId, int time, int roomId)
        {
            return ByClue.TryGetValue(Key(playerId, time, roomId), out int deviceId) ? deviceId : 0;
        }

        /// <summary>该设备在设备表里的详细活动类型；取不到或为 WNone 时返回 WNone。</summary>
        public static EWhatType WhatOfDevice(int deviceId)
        {
            if (deviceId == 0)
            {
                return EWhatType.WNone;
            }

            try
            {
                if (Managers.Data != null && Managers.Data.DeviceDic != null &&
                    Managers.Data.DeviceDic.TryGetValue(deviceId, out var device) && device != null)
                {
                    return device.WhatType;
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "按设备 id 查 WhatType 失败");
            }

            return EWhatType.WNone;
        }
    }
}
