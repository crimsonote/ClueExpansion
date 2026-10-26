#if TIMELINE   // 0.1.21 删除了回放时间线系统（TimelineRecorder / TimelineFrame 等）；定义 TIMELINE 才编译
using ClueDetail.Core;
using ClueDetail.Core.Timeline;
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
        /// 录制节拍：注册到回放管线的 <c>Plan</c> 阶段（唯一挂点是 Core/Timeline/Pipeline）。
        /// </summary>
        /// <remarks>
        /// 击杀帧（<c>OnMurder</c>）不参与 —— 它与本偏移机制无关，始终独立发送。
        /// </remarks>
        private static void RegisterPipeline()
        {
            TimelinePipeline.Register(
                "ShootReminder.Record",
                TimelinePipeline.Stage.Plan,
                100,
                (frame, surviveTime, stage) =>
                    !FeatureLoader.Enabled<ShootReminderFeature>() || ShouldCapture(surviveTime));
        }

        /// <summary>决定这一 tick 要不要采集（true = 放行原版采集）。</summary>
        private static bool ShouldCapture(int surviveTime)
        {
            // 泄露防护：未报告尸体的敏感期内不采集，避免出现「不在 _hidden、可正常查看」的帧。
            if (IsInUnreportedGuard(surviveTime))
            {
                return false;
            }

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

        /// <summary>
        /// 进入调查阶段：对**未报告**（IsReport == false）的尸体，补一帧隐藏点占位，
        /// 让时间线至少出现一个红色 --:--。
        /// </summary>
        /// <remarks>
        /// 已报告的尸体不处理（时间正常显示）。占位帧直接走 <c>Replicator.Participants</c>，
        /// 不经 <c>TimelineRecorder.Send</c>，避免被时钟破坏的 Prefix 缓存/打乱。
        /// </remarks>
        [HarmonyPatch(typeof(ServerGameRoom), "StartDetective")]
        internal static class DetectiveHook
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!FeatureLoader.Enabled<ShootReminderFeature>()) return;

                try
                {
                    PlaceHiddenFrames();
                }
                catch (global::System.Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "拍摄提醒：补隐藏点占位失败。");
                }
            }
        }

        private static void PlaceHiddenFrames()
        {
            ServerGameRoom room = ServerGameRoom.Instance;
            ServerTimelineRecorder recorder = ServerTimelineRecorder.Instance;
            if (room == null || room.CorpseMetas == null || recorder == null || recorder.Offset < 0)
            {
                return;
            }

            int offset = recorder.Offset;
            int first = TimelinePipeline.FirstRegular(offset);
            int last = TimelinePipeline.LastRegular(offset);
            int sent = 0;

            foreach (ServerGameRoom.CorpseMeta meta in room.CorpseMetas.Values)
            {
                if (meta == null || meta.IsReport)
                {
                    continue;       // 已报告 ⇒ 时间正常显示，不占位
                }

                int hidden = HiddenWindow.Predict(meta.DeadTime, meta.VictimId, offset, first, last);
                if (hidden <= 0)
                {
                    continue;
                }

                global::Server.Game.Replicator.Participants(new S_TIMELINE_FRAME
                {
                    Offset = offset,
                    Frame = new TimelineFrame { Time = hidden },
                });
                sent++;
            }

            if (sent > 0)
            {
                global::ClueDetail.Core.Log.Info(
                    $"拍摄提醒：调查阶段补 {sent} 帧隐藏点占位（客户端显示 --:--）。");
            }
        }
        /// <summary>是否落在某具未报告尸体的敏感期内。</summary>
        private static bool IsInUnreportedGuard(int surviveTime)
        {
            int guard = GuardSecondsValue;
            if (guard <= 0)
            {
                return false;
            }

            ServerGameRoom room = ServerGameRoom.Instance;
            if (room == null || room.CorpseMetas == null)
            {
                return false;
            }

            foreach (ServerGameRoom.CorpseMeta meta in room.CorpseMetas.Values)
            {
                if (meta != null
                    && !meta.IsReport
                    && global::System.Math.Abs(surviveTime - meta.DeadTime) <= guard)
                {
                    return true;
                }
            }

            return false;
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
#endif