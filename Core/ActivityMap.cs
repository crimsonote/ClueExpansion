using System;
using System.Collections.Generic;
using Protocol;

namespace ClueDetail.Core
{
    /// <summary>
    /// 「线索 → 设备」关联表（客户端侧自采集）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 服务端未装本模块、或该条线索是扫描得来的（<c>S_SCAN_DEVICE</c> 只带 <c>InfoList</c>、
    /// **协议里没有设备号**，0.1.18 :127846）时，客户端手里唯一能还原「这台设备的详细活动」
    /// 的途径就是自己记住"刚才扫的是哪台设备"。
    /// </para>
    /// <para>
    /// 关联方式：发起扫描时把 <c>(设备号, 发起时刻)</c> 压进队列，<c>S_SCAN_DEVICE</c> 到达时
    /// **在队列里找第一个"房间对得上"的条目**配上去（不是只看队首）。键用
    /// <c>(PlayerId, Time, RoomId)</c> —— 与 <c>ClueManager.AlreadyExistClue</c> 的原版去重口径一致。
    /// </para>
    /// <para>
    /// <b>2026-10-19 修（线索管线评估的 F5）</b>：旧实现只校验队首，一旦某个请求
    /// **被服务端拒掉**（<c>DeviceManager.Scan</c> 挡 <c>player.IsSpectator</c>）或**没有响应**，
    /// 那个死条目会永远卡在队首 —— 之后每个响应都对不上它，**整轮配对全部失效**
    /// （于是 ② 永远 miss，只能退到 ③/④ 去猜房间里的设备）。现在三条改进：
    /// <list type="number">
    /// <item><b>TTL</b>：请求超过 <see cref="PendingTtlSeconds"/> 秒仍无响应就丢弃（被拒 / 丢包都能自愈）；</item>
    /// <item><b>搜索而不是取队首</b>：按房间在队列里找匹配项，单个错位不再拖垮整条队列；</item>
    /// <item><b>空响应</b>按"最老且已至少过了扫描条时间"的那条记账（空响应不带房间号，无法校验）。</item>
    /// </list>
    /// ⚠️ 仍**做不到 100% 精确**：协议里没有设备号，同房间多台设备并发扫描时只能靠"响应顺序 = 请求顺序"
    /// 这个前提。要彻底精确得让服务端在 <c>S_SCAN_DEVICE</c> 上**附带来源设备号**（用 protobuf
    /// 未知字段挂在包上，未装模块的端原样忽略）——见 <c>docs/线索管线.md</c> §5 F5 的"彻底修法"。
    /// </para>
    /// <para>纯内存、只读游戏数据，不参与任何发包以外的行为。</para>
    /// </remarks>
    internal static class ActivityMap
    {
        private static readonly Dictionary<string, int> ByClue = new Dictionary<string, int>();

        private struct Pending
        {
            public int DeviceId;
            public float At;
        }

        private static readonly List<Pending> PendingScans = new List<Pending>();

        /// <summary>
        /// 待响应请求的存活时间（秒）。扫描条本身 2 秒（<c>DeviceBase.Scan</c>），加上网络往返与服务端
        /// 处理，留足余量；超过它还没响应就认定"这次请求不会有结果了"（被拒 / 对端没装模块）。
        /// </summary>
        private const float PendingTtlSeconds = 12f;

        /// <summary>空响应的配对下限：请求发出后至少过了这么久，才可能是眼前这条空响应。</summary>
        private const float MinAgeForEmpty = 1.5f;

        /// <summary>本局见过的最大 <c>SurviveTime</c>：用来识别"换局了"（见 <see cref="ResetIfNewRound"/>）。</summary>
        private static int _roundMaxTime;

        public static void Clear()
        {
            ByClue.Clear();
            PendingScans.Clear();
            _roundMaxTime = 0;
        }

        /// <summary>
        /// 跨局清理：<c>SurviveTime</c> 在一局内只增不减，明显回退就说明换局了。
        /// </summary>
        /// <remarks>
        /// ⚠️ 2026-10-19 查出的隐患：<see cref="Clear"/> 此前**从未被调用**，而关联表的键里带 <c>Time</c>
        /// （每局从 0 重新计时）⇒ 上一局的 (人, 时刻, 房间) 会命中这一局的线索，让 ② 设备关联查错设备。
        /// 留 60 秒余量，避免把"时间污染把显示时刻冻结成较小值"误判成换局。
        /// </remarks>
        private static void ResetIfNewRound(int time)
        {
            if (time + 60 < _roundMaxTime)
            {
                Clear();
                _roundMaxTime = time;
                return;
            }

            if (time > _roundMaxTime)
            {
                _roundMaxTime = time;
            }
        }

        public static string Key(int playerId, int time, int roomId)
        {
            return playerId + ":" + time + ":" + roomId;
        }

        // ── 待响应队列 ──────────────────────────────────────────────────────

        public static void QueueScan(int deviceId)
        {
            if (deviceId == 0)
            {
                return;
            }

            DropExpired();
            PendingScans.Add(new Pending { DeviceId = deviceId, At = Now });
        }

        public static int PendingCount
        {
            get
            {
                DropExpired();
                return PendingScans.Count;
            }
        }

        /// <summary>
        /// 为一条扫描结果取走它对应的设备号：在队列里找**第一个房间对得上**的待响应条目。
        /// 找不到（房间都不匹配）时返回 0，并且**不动队列**——避免错位扩散。
        /// </summary>
        public static int TakeScanFor(int roomId)
        {
            DropExpired();
            if (roomId == 0 || PendingScans.Count == 0)
            {
                return 0;
            }

            for (int i = 0; i < PendingScans.Count; i++)
            {
                if (!RoomMatches(PendingScans[i].DeviceId, roomId))
                {
                    continue;
                }

                int deviceId = PendingScans[i].DeviceId;
                PendingScans.RemoveAt(i);
                return deviceId;
            }

            return 0;
        }

        /// <summary>
        /// 取走队首设备号（0 = 队列为空）。空响应走这条：它不带房间号，没法校验，
        /// 只能取"最老、且已经过了扫描条时间"的那条。
        /// </summary>
        public static int TakeScan()
        {
            DropExpired();
            if (PendingScans.Count == 0)
            {
                return 0;
            }

            float now = Now;
            if (now - PendingScans[0].At < MinAgeForEmpty && PendingScans.Count == 1)
            {
                return 0;
            }

            int deviceId = PendingScans[0].DeviceId;
            PendingScans.RemoveAt(0);
            return deviceId;
        }

        /// <summary>队列里那台设备所在的房间，是否就是这条线索所在的房间。</summary>
        public static bool MatchesPendingRoom(int roomId)
        {
            DropExpired();
            if (PendingScans.Count == 0 || roomId == 0)
            {
                return false;
            }

            return RoomMatches(PendingScans[0].DeviceId, roomId);
        }

        /// <summary>丢掉超过 TTL 的待响应请求（被拒 / 没响应都能自愈），并记一次日志。</summary>
        private static void DropExpired()
        {
            if (PendingScans.Count == 0)
            {
                return;
            }

            float now = Now;
            int dropped = 0;
            for (int i = PendingScans.Count - 1; i >= 0; i--)
            {
                if (now - PendingScans[i].At > PendingTtlSeconds)
                {
                    PendingScans.RemoveAt(i);
                    dropped++;
                }
            }

            if (dropped > 0)
            {
                Log.Info(
                    $"线索关联：{dropped} 条扫描请求超过 {PendingTtlSeconds:F0} 秒没有响应，已丢弃" +
                    "（多半是被服务端拒绝，或对端没装本模块）。");
            }
        }

        private static float Now => UnityEngine.Time.realtimeSinceStartup;

        /// <summary>设备表里那台设备的房间类型，是否等于线索房间的房间类型。</summary>
        private static bool RoomMatches(int deviceId, int roomId)
        {
            if (deviceId == 0 || roomId == 0)
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

                if (!data.DeviceDic.TryGetValue(deviceId, out var device) || device == null)
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

            ResetIfNewRound(time);
            ByClue[Key(playerId, time, roomId)] = deviceId;
        }

        public static int DeviceOf(int playerId, int time, int roomId)
        {
            ResetIfNewRound(time);
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
