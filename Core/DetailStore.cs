using System.Collections.Generic;

namespace ClueDetail.Core
{
    /// <summary>
    /// 房主侧的「线索 → 详细活动」旁路表。
    /// </summary>
    /// <remarks>
    /// 键是 <c>(PlayerId, Time, RoomId)</c> —— 与下发给客户端的 <c>CdClueDetail</c> 完全一致，
    /// 也与客户端 <c>ClueManager.AlreadyExistClue</c> 的原版去重口径一致。
    /// 这样服务端扫描某台设备时，能按同样的键把活动捞回来单播给扫描者。
    /// </remarks>
    internal static class DetailStore
    {
        private static readonly Dictionary<string, int> Map = new Dictionary<string, int>();

        public static int Count => Map.Count;

        private static string Key(int playerId, int time, int roomId)
        {
            return playerId + ":" + time + ":" + roomId;
        }

        public static void Remember(int playerId, int time, int roomId, int what)
        {
            if (what != 0)
            {
                Map[Key(playerId, time, roomId)] = what;
            }
        }

        public static int Recall(int playerId, int time, int roomId)
        {
            return Map.TryGetValue(Key(playerId, time, roomId), out int what) ? what : 0;
        }

        public static void Clear()
        {
            Map.Clear();
        }
    }
}