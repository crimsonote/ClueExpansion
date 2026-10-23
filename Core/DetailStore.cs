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
                ResetIfNewRound(time);

            Map[Key(playerId, time, roomId)] = what;
            }
        }

        public static int Recall(int playerId, int time, int roomId)
        {
            ResetIfNewRound(time);

            return Map.TryGetValue(Key(playerId, time, roomId), out int what) ? what : 0;
        }

        /// <summary>本局见过的最大 SurviveTime：识别"换局"（键里带 Time，每局从 0 重算）。</summary>

        private static int _roundMaxTime;


        /// <summary>明显时间回退 ⇒ 换局了，清掉上一局的旁路表（2026-10-19 修：Clear 此前从未被调用）。</summary>

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


        public static void Clear()
        {
            Map.Clear();

            _roundMaxTime = 0;
        }
    }
}