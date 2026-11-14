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

        public static void Clear()
        {
            ByClue.Clear();
            PendingScans.Clear();
        }

        // ⚠️ 2026-11-14 删除「按线索 Time 回退判换局」的启发式（原 ResetIfNewRound）。
        // 那个判据是 `time + 60 < 本局见过的最大 Time ⇒ 换局了`，但**一局内线索时间本身就横跨数百秒**
        //（实机日志：435 → 689），而渲染/查询顺序不保证单调（平板、悬浮、补扫各自触发）⇒
        // 只要先查过一条较晚的线索、再查一条较早的，整张关联表连同待响应扫描队列就被误清，
        // ② 设备关联几乎永远 miss，只能退到按房间猜。换局清理改由
        // ClueRenderFeature.ClueClearHook 挂 `ClueManager.Clear`（回大厅 StartLobby 调用，
        // 0.1.21 :30109）显式执行。

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

            ByClue[Key(playerId, time, roomId)] = deviceId;
        }

        public static int DeviceOf(int playerId, int time, int roomId)
        {
            return ByClue.TryGetValue(Key(playerId, time, roomId), out int deviceId) ? deviceId : 0;
        }

        /// <summary>
        /// 用游戏自己的 <c>Server.Game.ClueMirror</c> 反查「这条线索属于哪台设备」。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>S_ADD_CLUE</c> 自带 <c>DeviceId</c>，客户端 <c>PacketHandler.Handle_S_ADD_CLUE</c>
        /// （0.1.21 :43621-43627）收到后进 <c>ClueMirror.Add(deviceId, info)</c>（:180795）——
        /// 这张表由**游戏自己维护**，不会因为我们清理自采集而失效。本模块原来的
        /// <see cref="Remember"/> 只在两个瞬间记录（扫描响应、广播），表一旦被清就丢；
        /// 这里在渲染时直接反查镜像，把"广播时没记上/被清掉"的关联补回来。
        /// </para>
        /// <para>
        /// ⚠️ 房主本机不走镜像（<c>Handle_S_ADD_CLUE</c> 的 <c>IsHost</c> 守卫，:43623），
        /// 这里自然查不到 —— 房主侧由字段 100 / 旁路表负责，不影响。
        /// </para>
        /// <para>
        /// 代价：只在 ② 未命中时调用，遍历本局设备表（几十台）各查一次镜像列表；
        /// 命中即 <see cref="Remember"/>，同一线索不会重复遍历。
        /// </para>
        /// </remarks>
        public static int FindInMirror(int playerId, int time, int roomId)
        {
            try
            {
                // 已经记过就不必遍历设备表（ClueWhat 走到这里时必然未命中，这里是给 AddClue 那条路省开销）。
                int known = DeviceOf(playerId, time, roomId);
                if (known != 0)
                {
                    return known;
                }

                var cache = Managers.Device != null ? Managers.Device.Cache : null;
                if (cache == null || cache.Count == 0)
                {
                    return 0;
                }

                foreach (var pair in cache)
                {
                    int deviceId = pair.Key;
                    if (deviceId == 0)
                    {
                        continue;
                    }

                    var clues = Server.Game.ClueMirror.Get(deviceId);
                    if (clues == null || clues.Count == 0)
                    {
                        continue;
                    }

                    for (int i = 0; i < clues.Count; i++)
                    {
                        var info = clues[i];
                        if (info != null && info.PlayerId == playerId &&
                            info.Time == time && info.RoomId == roomId)
                        {
                            Remember(playerId, time, roomId, deviceId);
                            return deviceId;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "线索镜像反查失败");
            }

            return 0;
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
