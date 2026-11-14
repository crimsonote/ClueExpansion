using System;
using System.Reflection;
using Protocol;
using ServerDeviceManager = Server.Game.DeviceManager;
using ServerGamePlayer = Server.Game.Player;
using ServerGameRoom = Server.Game.GameRoom;
using ServerTimeManager = Server.Game.TimeManager;

namespace ClueDetail.Features.Host.Meltdown
{
    /// <summary>
    /// 融毁逻辑：断电期间的倒计时倍率、停钟判定、秘密通道提示、终局统计。
    /// </summary>
    internal static class MeltdownLogic
    {
        private static readonly PropertyInfo RemainProperty =
            typeof(ServerTimeManager).GetProperty("RemainTime");

        private static readonly MethodInfo RemainSetter =
            RemainProperty == null ? null : RemainProperty.GetSetMethod(true);

        private static bool _hsModeEnabled;
        private static float _hsCheckedAt = -999f;

        /// <summary>
        /// 捉迷藏<b>模式</b>是否启用（读 HideAndSeek 的 cfg 段 <c>[HS_Mode] Enabled</c>）。
        /// 只装了插件但模式关着时，本功能照常生效。5 秒缓存，热读。
        /// </summary>
        internal static bool HideAndSeekModeEnabled()
        {
            if (UnityEngine.Time.realtimeSinceStartup - _hsCheckedAt < 5f)
            {
                return _hsModeEnabled;
            }

            _hsCheckedAt = UnityEngine.Time.realtimeSinceStartup;
            _hsModeEnabled = ReadHsModeEnabled();
            return _hsModeEnabled;
        }

        private static bool ReadHsModeEnabled()
        {
            try
            {
                string path = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "HideAndSeek.cfg");
                if (!System.IO.File.Exists(path))
                {
                    return false;
                }

                string section = null;
                foreach (string raw in System.IO.File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                    {
                        continue;
                    }

                    if (line[0] == '[')
                    {
                        section = line.Trim('[', ']').Trim();
                        continue;
                    }

                    if (!string.Equals(section, "HS_Mode", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    int eq = line.IndexOf('=');
                    if (eq < 0)
                    {
                        continue;
                    }

                    if (!line.Substring(0, eq).Trim().Equals("Enabled", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    bool value;
                    return bool.TryParse(line.Substring(eq + 1).Trim(), out value) && value;
                }
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Warn(
                    "断电融毁：读取捉迷藏模式状态失败（" + ex.Message + "）。");
            }

            return false;
        }

        /// <summary>本功能当前是否实际生效（开关 + 捉迷藏保护）。</summary>
        internal static bool IsActive()
        {
            string mode = MeltdownFeature.HideAndSeekMode == null
                ? "auto"
                : (MeltdownFeature.HideAndSeekMode.Value ?? "auto").Trim();
            if (mode.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (mode.Equals("auto", StringComparison.OrdinalIgnoreCase) && HideAndSeekModeEnabled())
            {
                return false;
            }

            return true;
        }

        /// <summary>当前断电期间的流逝倍率：停钟状态用 <c>PauseMultiplier</c>，否则用 <c>SpeedMultiplier</c>。</summary>
        internal static float Multiplier()
        {
            float value = MeltdownState.PauseThisRound
                ? (MeltdownFeature.PauseMultiplier == null ? 0f : MeltdownFeature.PauseMultiplier.Value)
                : (MeltdownFeature.SpeedMultiplier == null ? 1f : MeltdownFeature.SpeedMultiplier.Value);
            return value < 0f ? 0f : value;
        }

        /// <summary>
        /// 是否处于断电状态。**锁存**：2 台拉断后置位；必须**所有**电闸恢复（count == 0）
        /// 才解除 —— 修好一台（count == 1）仍然算断电，继续按融毁倍率流逝。
        /// </summary>
        internal static bool IsBlackout()
        {
            ServerDeviceManager manager = ServerDeviceManager.Instance;
            if (manager == null)
            {
                return false;
            }

            int count = manager.GetDisconnectFuseCount();
            if (count >= 2)
            {
                MeltdownState.BlackoutLatched = true;
            }
            else if (count == 0)
            {
                MeltdownState.BlackoutLatched = false;
            }

            return MeltdownState.BlackoutLatched;
        }

        /// <summary>
        /// <c>TimeManager.TickSurvive</c> 的 Prefix：原方法随后会 <c>RemainTime -= 1</c>，
        /// 这里先把值预调成「目标时间 + 1」，让原方法扣完并广播出目标值。
        /// </summary>
        internal static void BeforeTick()
        {
            if (!IsActive() || !IsBlackout())
            {
                return;
            }

            float multiplier = Multiplier();
            if (Math.Abs(multiplier - 1f) < 0.0001f)
            {
                return;
            }

            ServerTimeManager time = ServerTimeManager.Instance;
            if (time == null)
            {
                return;
            }

            float remain = time.RemainTime;
            if (remain <= 0f || RemainSetter == null)
            {
                return;
            }

            float target = remain - multiplier + 1f;
            float max = time.TotalTime;
            if (target < 0f)
            {
                target = 0f;
            }
            else if (target > max)
            {
                target = max;
            }

            RemainSetter.Invoke(time, new object[] { target });
        }

        /// <summary>每秒检查一次：开局第 N 秒或出现「白转黑」时发秘密通道提示。</summary>
        internal static void TickNotice(ServerGameRoom room)
        {
            if (!IsActive() || !MeltdownState.PauseThisRound || MeltdownState.NoticeSent)
            {
                return;
            }

            ServerTimeManager time = ServerTimeManager.Instance;
            if (time == null)
            {
                return;
            }

            int noticeSecond = MeltdownFeature.NoticeSecond == null ? 5 : MeltdownFeature.NoticeSecond.Value;
            bool byTime = noticeSecond > 0
                && MeltdownState.RoundStartSecond >= 0
                && time.SurviveTime - MeltdownState.RoundStartSecond >= noticeSecond;
            bool byColor = DetectWhiteToBlack(room);
            if (byTime || byColor)
            {
                SendNotice(room);
            }
        }

        private static bool DetectWhiteToBlack(ServerGameRoom room)
        {
            bool found = false;
            foreach (ServerGamePlayer player in room.Players)
            {
                if (player == null || player.PublicInfo == null)
                {
                    continue;
                }

                int id = player.PublicInfo.PlayerId;
                EPlayerColor color = player.Color;
                EPlayerColor previous;
                if (MeltdownState.LastColors.TryGetValue(id, out previous))
                {
                    if (previous == EPlayerColor.White
                        && (color == EPlayerColor.Black || color == EPlayerColor.Dark))
                    {
                        found = true;
                    }
                }

                MeltdownState.LastColors[id] = color;
            }

            return found;
        }

        private static void SendNotice(ServerGameRoom room)
        {
            MeltdownState.NoticeSent = true;

            if (MeltdownFeature.NoticeEnabled != null && !MeltdownFeature.NoticeEnabled.Value)
            {
                return;
            }

            float ratio = MeltdownFeature.RatioThreshold == null
                ? 0.3f
                : MeltdownFeature.RatioThreshold.Value;
            string template = MeltdownFeature.NoticeText == null
                ? null
                : MeltdownFeature.NoticeText.Value;
            if (string.IsNullOrWhiteSpace(template))
            {
                template = "【断电融毁】近期融毁胜利比例大于{ratio}%，本局断电时倒计时流逝将会暂停。";
            }

            string text = template.Replace(
                "{ratio}", ((int)Math.Round(ratio * 100f)).ToString());

            foreach (ServerGamePlayer player in room.Players)
            {
                if (player == null || player.Session == null)
                {
                    continue;
                }

                // 只告知黑（杀手）与黑幕：Dark = 黑幕，Black = 黑（见 AGENTS §4.9）。
                if (player.Color != EPlayerColor.Black && player.Color != EPlayerColor.Dark)
                {
                    continue;
                }

                try
                {
                    player.Session.Send(new S_CHAT_MESSAGE
                    {
                        Type = EChatType.SecretChat,
                        Text = text,
                        PlayerId = 0,
                    });
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Warn(
                        "断电融毁：秘密通道提示发送失败（" + ex.Message + "）。");
                }
            }
        }

        /// <summary><c>GameOver</c> 时记录本局是否「时间流空」。</summary>
        internal static void RecordGameOver()
        {
            if (!IsActive() || MeltdownState.MeltRecorded)
            {
                return;
            }

            MeltdownState.MeltRecorded = true;
            ServerTimeManager time = ServerTimeManager.Instance;
            bool melt = time != null && time.RemainTime <= 0f;
            MeltdownState.Append(melt);
            global::ClueDetail.Core.Log.Info(
                "断电融毁：本局终局记录为" + (melt ? "融毁胜利" : "非融毁")
                + "，近期融毁比例 " + (int)Math.Round(MeltdownState.Ratio() * 100f) + "%。");
        }

        /// <summary>进入新一局（<c>ChangeGameState(Survive)</c>）时调用。</summary>
        internal static void BeginRound()
        {
            if (!IsActive())
            {
                return;
            }

            MeltdownState.BeginRound();
            ServerTimeManager time = ServerTimeManager.Instance;
            if (time != null)
            {
                MeltdownState.RoundStartSecond = time.SurviveTime;
            }

            if (MeltdownState.PauseThisRound)
            {
                global::ClueDetail.Core.Log.Info(
                    "断电融毁：近期比例 " + (int)Math.Round(MeltdownState.Ratio() * 100f)
                    + "% > 阈值 " + (int)Math.Round(
                        (MeltdownFeature.RatioThreshold == null ? 0.3f : MeltdownFeature.RatioThreshold.Value) * 100f)
                    + "%，本局断电时停钟。");
            }
        }
    }
}
