﻿﻿﻿using ClueDetail.Core;
using HarmonyLib;
using Protocol;
using ServerGamePlayer = Server.Game.Player;
using ServerGameRoom = Server.Game.GameRoom;
using ServerTimeManager = Server.Game.TimeManager;
using ServerTimelineRecorder = Server.Game.TimelineRecorder;

namespace ClueDetail.Features.Host.ShootReminder
{
    internal partial class ShootReminderFeature
    {
        /// <summary>本秒是否已经提醒过（<c>SurvivalTick</c> 每帧都会调）。</summary>
        private static int _lastFiredSecond = -1;

        /// <summary>
        /// 每帧检查「下一帧拍摄」是否在 1 秒后，提前发变暗。
        /// </summary>
        /// <remarks>
        /// <para>
        /// ⚠️ 不能挂在 <c>TimelineRecorder.Send</c> 的 Postfix：那是帧**已经发出去之后**，
        /// 黑屏只能从拍摄之后开始，画面上就会慢一拍（用户 2026-10-24 实测「慢了两秒」）。
        /// </para>
        /// <para>
        /// 拍摄节拍：<c>Offset + 10k</c>（每 10 分钟一帧，<c>Offset</c> = 每局 0–10 的起始偏移）。
        /// 提前 1 秒 = <c>rel % 10 == 9</c>；「每小时第 2 次」= <c>rel % 60 == 9</c>。
        /// 客户端 <c>DevicesManager.PlayEffect</c> 的 BlackOut 是 2 秒，中心正好落在拍摄时刻。
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(ServerGameRoom), "SurvivalTick")]
        internal static class TickHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!FeatureLoader.Enabled<ShootReminderFeature>()) return;

                try
                {
                    Tick();
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "拍摄提醒：每帧检查失败。");
                }
            }
        }

        /// <summary>
        /// 回放记录频率（简化 cron）：Prefix 决定原版 <c>OnSurvivalTick</c> 这一 tick 要不要采集。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 原版只在 <c>surviveTime % 10 == Offset</c> 时 <c>Capture</c> 一帧（10 分钟一帧，
        /// <c>Offset</c> = 每局 0–10 的随机起点）。
        /// </para>
        /// <para>
        /// 支持：<c>default</c>（原版）、<c>*/N</c>（每 N 分钟，N ≥ 10）、
        /// <c>M */H</c>（每 H 小时的 M 分钟）、<c>never</c>（一次都不记）。
        /// 无法解析时一律放行，绝不因为配错而让回放完全断掉。
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(ServerTimelineRecorder), nameof(ServerTimelineRecorder.OnSurvivalTick))]
        internal static class RecordCronHook
        {
            [HarmonyPrefix]
            private static bool Prefix(int surviveTime)
            {
                if (!FeatureLoader.Enabled<ShootReminderFeature>()) return true;

                try
                {
                    return ShouldCapture(surviveTime);
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "拍摄提醒：记录频率判断失败（放行）。");
                    return true;
                }
            }
        }

        /// <summary>按 <see cref="RecordCronValue"/> 判断这一 tick 是否采集。</summary>
        private static bool ShouldCapture(int surviveTime)
        {
            string cron = (RecordCronValue ?? string.Empty).Trim().ToLowerInvariant();
            if (cron.Length == 0 || cron == "default")
            {
                return true;        // 原版自己判断
            }

            if (cron == "never" || cron == "-" || cron == "0")
            {
                return false;
            }

            ServerTimelineRecorder recorder = ServerTimelineRecorder.Instance;
            int offset = recorder != null ? recorder.Offset : 0;
            int rel = surviveTime - offset;
            if (rel < 0)
            {
                return false;
            }

            // */N —— 每 N 分钟（游戏分钟 = 秒）
            if (cron.StartsWith("*/"))
            {
                if (int.TryParse(cron.Substring(2), out int n))
                {
                    if (n < 10) n = 10;     // 不比原版更密
                    return rel % n == 0;
                }

                return true;
            }

            // M */H —— 每 H 小时的 M 分钟
            string[] parts = cron.Split(new[] { ' ' }, global::System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].StartsWith("*/"))
            {
                if (int.TryParse(parts[0], out int minute)
                    && int.TryParse(parts[1].Substring(2), out int hours)
                    && hours > 0)
                {
                    minute = global::System.Math.Max(0, global::System.Math.Min(59, minute));
                    return rel % (hours * 60) == minute;
                }
            }

            return true;    // 解析失败 ⇒ 放行
        }
        private static void Tick()
        {
            ServerGameRoom room = ServerGameRoom.Instance;
            if (room == null || room.State != EGameState.Survive)
            {
                return;
            }

            ServerTimelineRecorder recorder = ServerTimelineRecorder.Instance;
            ServerTimeManager tm = ServerTimeManager.Instance;
            if (recorder == null || tm == null)
            {
                return;
            }

            int now = tm.SurviveTime;
            if (now == _lastFiredSecond)
            {
                return;     // 同一秒只处理一次
            }

            int rel = now - recorder.Offset;
            if (rel < 0)
            {
                return;
            }

            // 拍摄发生在 rel % 10 == 0；提前 LeadSeconds 秒，让 2 秒黑屏的中心落在拍摄时刻。
            int lead = LeadSecondsValue;
            if (lead < 0) lead = 0;
            if (lead > 5) lead = 5;
            bool fire = EveryShotValue
                ? rel % 10 == (10 - lead) % 10
                : rel % 60 == 10 - lead;
            if (!fire)
            {
                return;
            }

            _lastFiredSecond = now;
            SendBlackout(room, now, recorder.Offset);
        }

        private static void SendBlackout(ServerGameRoom room, int now, int offset)
        {
            if (room.Players == null)
            {
                return;
            }

            bool all = ToAllValue;
            EEffectType effect = EffectChoiceValue == 1 ? EEffectType.MineBombVfx : EEffectType.BlackOutVfx;
            ServerGamePlayer mastermind = room.MasterMind;
            int sent = 0;

            for (int i = 0; i < room.Players.Count; i++)
            {
                ServerGamePlayer p = room.Players[i];
                if (p == null || p.Session == null || p.PublicInfo == null || !p.IsAlive)
                {
                    continue;
                }

                bool hit = all
                    || p.Color == EPlayerColor.Dark
                    || p.Color == EPlayerColor.Black
                    || (mastermind != null && p == mastermind);
                if (!hit)
                {
                    continue;
                }

                p.Session.Send(new S_PLAY_EFFECT
                {
                    Type = effect,
                });
                sent++;
            }

            if (sent > 0)
            {
                global::ClueDetail.Core.Log.Info(
                    $"拍摄提醒：t={now}（拍摄偏移 {offset}，拍摄在第 {now + 1} 秒）→ 变暗 {sent} 人（{effect}）。");
            }
        }

        /// <summary>换局重置「本秒已提醒」标记。</summary>
        internal static void Reset()
        {
            _lastFiredSecond = -1;
        }
    }
}
