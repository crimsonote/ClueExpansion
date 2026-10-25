using ClueDetail.Core;
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
        /// <summary>本局的录制偏移（录制与提醒共用）。负数 = 还没生成。</summary>
        private static int _offset = -1;

        /// <summary>本秒是否已经提醒过。</summary>
        private static int _lastFiredSecond = -1;

        /// <summary>每局开始（<c>StartSurvive</c>）时生成本模块自己的偏移。</summary>
        [HarmonyPatch(typeof(ServerTimelineRecorder), nameof(ServerTimelineRecorder.StartSurvive))]
        internal static class StartSurviveHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!FeatureLoader.Enabled<ShootReminderFeature>()) return;

                _offset = RollOffset();
                _lastFiredSecond = -1;
            }
        }

        /// <summary>
        /// 录制周期：Prefix 决定原版 <c>OnSurvivalTick</c> 这一 tick 要不要采集。
        /// </summary>
        /// <remarks>
        /// 击杀帧（<c>OnMurder</c>）**不挂** —— 它与本偏移机制无关，始终独立发送。
        /// </remarks>
        [HarmonyPatch(typeof(ServerTimelineRecorder), nameof(ServerTimelineRecorder.OnSurvivalTick))]
        internal static class RecordHook
        {
            [HarmonyPrefix]
            private static bool Prefix(int surviveTime)
            {
                if (!FeatureLoader.Enabled<ShootReminderFeature>()) return true;

                try
                {
                    string cron = RecordCronValue;
                    if (CronSchedule.IsDefault(cron))
                    {
                        return true;        // 原版自己判断
                    }

                    if (CronSchedule.IsNever(cron))
                    {
                        return false;
                    }

                    return CronSchedule.Matches(cron, surviveTime - CurrentOffset());
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "拍摄提醒：录制频率判断失败（放行）。");
                    return true;
                }
            }
        }

        /// <summary>提醒：每帧检查是否到了「录制节拍 - LeadSeconds」。</summary>
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

        private static void Tick()
        {
            ServerGameRoom room = ServerGameRoom.Instance;
            ServerTimeManager tm = ServerTimeManager.Instance;
            if (room == null || room.State != EGameState.Survive || tm == null)
            {
                return;
            }

            int now = tm.SurviveTime;
            if (now == _lastFiredSecond)
            {
                return;
            }

            int lead = LeadSecondsValue;
            if (lead < 0) lead = 0;
            if (lead > 10) lead = 10;

            string cron = EffectiveReminderCron();
            int rel = now - CurrentOffset();
            if (!CronSchedule.Matches(cron, rel + lead))
            {
                return;
            }

            _lastFiredSecond = now;
            SendBlackout(room, now, CurrentOffset(), cron);
        }

        /// <summary>提醒用的节拍：<c>ReminderCron</c> 非 default 时用它；否则跟录制 / EveryShot。</summary>
        private static string EffectiveReminderCron()
        {
            string cron = ReminderCronValue;
            if (!CronSchedule.IsDefault(cron))
            {
                return cron;
            }

            return EveryShotValue ? "*/10 *" : RecordCronValue;
        }

        private static int RollOffset()
        {
            int min = OffsetMinValue;
            int max = OffsetMaxValue;
            if (max <= min)
            {
                return -1;      // 配置无效 ⇒ 退回原版 Offset
            }

            return new global::System.Random().Next(min, max);
        }

        /// <summary>本模块偏移；没生成（或配置无效）时退回原版 <c>TimelineRecorder.Offset</c>。</summary>
        private static int CurrentOffset()
        {
            if (_offset >= 0)
            {
                return _offset;
            }

            ServerTimelineRecorder recorder = ServerTimelineRecorder.Instance;
            return recorder != null ? recorder.Offset : 0;
        }

        private static void SendBlackout(ServerGameRoom room, int now, int offset, string cron)
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
                    $"拍摄提醒：t={now}（偏移 {offset}，cron={cron}）→ {effect} {sent} 人。");
            }
        }

        /// <summary>换局重置。</summary>
        internal static void Reset()
        {
            _offset = -1;
            _lastFiredSecond = -1;
        }
    }
}
