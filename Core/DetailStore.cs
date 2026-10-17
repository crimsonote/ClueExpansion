using System.Collections.Generic;

namespace ClueDetail.Core
{
    /// <summary>
    /// 房主侧的「线索 → 详细活动」旁路表。
    /// </summary>
    /// <remarks>
    /// 0.1.18 的 <c>PropositionInfo</c> 不再携带 WhatType，而线索的唯一权威来源是设备表
    /// <c>Data.DeviceData.WhatType</c>。问题是"线索 → 设备"这一步在下发路径里是丢失的
    /// （S_SCAN_DEVICE 不带 DeviceId；S_ADD_CLUE 带，但 Info 里没有 what）。
    /// <para>
    /// 于是在**服务端写入线索的那一刻**把 (设备, 玩家, 时间) → WhatType 记在这里，
    /// 之后任何下发路径都能按同样的键把它捞回来，补进 protobuf 未知字段（见 <see cref="UnknownFields"/>）。
    /// </para>
    /// <para>纯内存，键的粒度与线索本身一致（30 秒窗口 + 玩家 + 设备）。</para>
    /// </remarks>
    internal static class DetailStore
    {
        private static readonly Dictionary<string, int> Map = new Dictionary<string, int>();

        private static string Key(int deviceId, int playerId, int time)
        {
            return deviceId + ":" + playerId + ":" + time;
        }

        public static void Remember(int deviceId, int playerId, int time, int what)
        {
            if (deviceId == 0 || what == 0)
            {
                return;
            }

            Map[Key(deviceId, playerId, time)] = what;
        }

        public static int Recall(int deviceId, int playerId, int time)
        {
            return Map.TryGetValue(Key(deviceId, playerId, time), out int what) ? what : 0;
        }

        public static void Clear()
        {
            Map.Clear();
        }
    }
}
