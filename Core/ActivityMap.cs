using System;
using System.Collections.Generic;
using Protocol;

namespace ClueDetail.Core
{
    /// <summary>
    /// 「线索 → 设备」关联表（客户端侧自采集）。
    /// </summary>
    /// <remarks>
    /// 服务端未装本模块、或该条线索是扫描得来的（<c>S_SCAN_DEVICE</c> 只带 InfoList、不带 DeviceId）时，
    /// 客户端手里唯一能还原「这台设备的详细活动」的途径就是自己记住"刚才扫的是哪台设备"。
    /// <para>
    /// 关联方式：发起扫描时把设备号压进队列，<c>S_SCAN_DEVICE</c> 到达时取队首把整批线索登记上去。
    /// 键用 (PlayerId, Time, RoomId) —— 与 <c>ClueManager.AlreadyExistClue</c> 的原版去重口径一致。
    /// </para>
    /// <para>
    /// 队列而不是单个变量：死者/旁观者的"自动补扫"会连续发几十个请求。服务端是同步顺序处理的，
    /// 所以响应顺序等于请求顺序；但**请求被拒**（服务端 <c>DeviceManager.Scan</c> 会挡
    /// <c>player.IsSpectator</c>）时不会有响应，因此配对前先用房间号校验队首
    /// （<see cref="MatchesPendingRoom"/>），对不上就认为发生了错位，本次不关联也不出队。
    /// </para>
    /// <para>纯内存、只读游戏数据，不参与任何发包以外的行为。</para>
    /// </remarks>
    internal static class ActivityMap
    {
        private static readonly Dictionary<string, int> ByClue = new Dictionary<string, int>();
        private static readonly Queue<int> PendingScans = new Queue<int>();

        public static void Clear()
        {
            ByClue.Clear();
            PendingScans.Clear();
        }

        public static string Key(int playerId, int time, int roomId)
        {
            return playerId + ":" + time + ":" + roomId;
        }

        // ── 待响应队列 ──────────────────────────────────────────────────────

        public static void QueueScan(int deviceId)
        {
            if (deviceId != 0)
            {
                PendingScans.Enqueue(deviceId);
            }
        }

        public static int PendingCount => PendingScans.Count;

        /// <summary>取出队首设备号并出队（0 = 队列为空）。</summary>
        public static int TakeScan()
        {
            return PendingScans.Count > 0 ? PendingScans.Dequeue() : 0;
        }

        /// <summary>队首设备所在的房间，是否就是这条线索所在的房间。</summary>
        public static bool MatchesPendingRoom(int roomId)
        {
            if (PendingScans.Count == 0 || roomId == 0)
            {
                return false;
            }

            try
            {
                var data = Managers.Data;
                if (data == null || data.DeviceDic == null || data.RoomDic == null)
                {
                    return false;
                }

                if (!data.DeviceDic.TryGetValue(PendingScans.Peek(), out var device) || device == null)
                {
                    return false;
                }

                if (!data.RoomDic.TryGetValue(roomId, out var room) || room == null)
                {
                    return false;
                }

                return device.RoomType == room.Type;
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "校验待响应扫描设备失败");
                return false;
            }
        }

        // ── 关联表 ──────────────────────────────────────────────────────────

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
