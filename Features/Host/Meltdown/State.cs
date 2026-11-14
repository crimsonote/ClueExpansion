using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Protocol;

namespace ClueDetail.Features.Host.Meltdown
{
    /// <summary>
    /// 融毁统计与本局状态：最近 N 局的「时间流空」记录（跨重启保留），
    /// 以及本局是否停钟、是否已告知、是否已记录终局。
    /// </summary>
    internal static class MeltdownState
    {
        private static readonly List<bool> Recent = new List<bool>();
        private static bool _loaded;

        /// <summary>本局断电期间是否停钟。</summary>
        internal static bool PauseThisRound;

        /// <summary>断电锁存：2 台拉断后置位，直到**所有**电闸恢复（count == 0）才解除。</summary>
        internal static bool BlackoutLatched;

        /// <summary>本局是否已发过秘密通道提示。</summary>
        internal static bool NoticeSent;

        /// <summary>本局是否已记录终局（防 GameOver 重复调用）。</summary>
        internal static bool MeltRecorded;

        /// <summary>本局进入 Survive 时的 <c>SurviveTime</c>；&lt; 0 表示还没进。</summary>
        internal static int RoundStartSecond = -1;

        /// <summary>上一次扫描到的玩家颜色（用于检测「白转黑」）。</summary>
        internal static readonly Dictionary<int, EPlayerColor> LastColors =
            new Dictionary<int, EPlayerColor>();

        private static string FilePath
        {
            get { return Path.Combine(Paths.ConfigPath, "ClueDetail.meltdown.txt"); }
        }

        internal static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            Load();
        }

        private static void Load()
        {
            Recent.Clear();
            try
            {
                if (!File.Exists(FilePath))
                {
                    return;
                }

                foreach (string line in File.ReadAllLines(FilePath))
                {
                    bool value;
                    if (bool.TryParse(line.Trim(), out value))
                    {
                        Recent.Add(value);
                    }
                }
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Warn(
                    "断电融毁：读取历史统计失败（" + ex.Message + "）。");
            }

            Trim();
        }

        private static void Trim()
        {
            int rounds = MeltdownFeature.RecentRounds == null ? 10 : MeltdownFeature.RecentRounds.Value;
            if (rounds < 1)
            {
                rounds = 1;
            }

            while (Recent.Count > rounds)
            {
                Recent.RemoveAt(0);
            }
        }

        /// <summary>最近 N 局里「融毁胜利」的比例；没有记录时为 0。</summary>
        internal static float Ratio()
        {
            EnsureLoaded();
            if (Recent.Count == 0)
            {
                return 0f;
            }

            int count = 0;
            foreach (bool value in Recent)
            {
                if (value)
                {
                    count++;
                }
            }

            return (float)count / Recent.Count;
        }

        /// <summary>追加一局结果（true = 融毁胜利）并落盘。</summary>
        internal static void Append(bool melt)
        {
            EnsureLoaded();
            Recent.Add(melt);
            Trim();

            try
            {
                var lines = new List<string>();
                foreach (bool value in Recent)
                {
                    lines.Add(value ? "1" : "0");
                }

                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Warn(
                    "断电融毁：写入历史统计失败（" + ex.Message + "）。");
            }
        }

        /// <summary>进入新一局：按历史比例决定本局是否停钟，并清空本局状态。</summary>
        internal static void BeginRound()
        {
            EnsureLoaded();
            bool pause = MeltdownFeature.PauseWhenOverRatio != null
                && MeltdownFeature.PauseWhenOverRatio.Value
                && Ratio() > (MeltdownFeature.RatioThreshold == null ? 0.3f : MeltdownFeature.RatioThreshold.Value);
            PauseThisRound = pause;
            BlackoutLatched = false;
            NoticeSent = false;
            MeltRecorded = false;
            RoundStartSecond = -1;
            LastColors.Clear();
        }

        internal static void Reset()
        {
            PauseThisRound = false;
            BlackoutLatched = false;
            NoticeSent = false;
            MeltRecorded = false;
            RoundStartSecond = -1;
            LastColors.Clear();
        }
    }
}
