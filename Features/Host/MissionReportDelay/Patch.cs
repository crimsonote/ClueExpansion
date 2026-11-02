using System;
using System.Reflection;
using ClueDetail.Core;
using HarmonyLib;
using Protocol;
using ServerCorpse = Server.Game.Corpse;
using ServerGameRoom = Server.Game.GameRoom;
using ServerTimeManager = Server.Game.TimeManager;

namespace ClueDetail.Features.Host.MissionReportDelay
{
    internal partial class MissionReportDelayFeature
    {
        private static Type _missionManagerType;
        private static MethodInfo _triggerMethod;

        /// <summary>本次推迟已经发生过一次（防无限推迟）。</summary>
        private static bool _delayed;

        /// <summary>推迟期间由我们按下的暂停（退出时恢复原值）。</summary>
        private static bool _pausedByUs;

        private static bool _prevPaused;

        /// <summary>
        /// <c>MissionManager.TriggerAllClearEnd</c> 是 private；<c>MissionManager</c> 本身是
        /// internal ⇒ 只能按名字找类型 + 方法。
        /// </summary>
        [HarmonyPatch]
        internal static class TriggerAllClearEndHook
        {
            [HarmonyTargetMethod]
            private static MethodBase Target()
            {
                _missionManagerType = AccessTools.TypeByName("Server.Game.MissionManager");
                _triggerMethod = _missionManagerType == null
                    ? null
                    : AccessTools.Method(_missionManagerType, "TriggerAllClearEnd");
                return _triggerMethod;
            }

            [HarmonyPrefix]
            private static bool Prefix()
            {
                if (!FeatureLoader.Enabled<MissionReportDelayFeature>()) return true;

                try
                {
                    // Decide 返回 true = 拦截原版，false = 放行。
                    return Decide();
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "任务报告延迟：判断失败（放行原版）。");
                    return true;
                }
            }
        }

        /// <returns>true = 拦截原版（本次不做任何事），false = 放行原版。</returns>
        private static bool Decide()
        {
            ServerGameRoom room = ServerGameRoom.Instance;
            ServerTimeManager tm = ServerTimeManager.Instance;
            if (room == null || tm == null)
            {
                return false;
            }

            // ⚠️ 已经不在生存阶段（手动报告了尸体、或别的原因已经进调查/结算）：
            // 这一次 TriggerAllClearEnd 什么都不该做 —— 既不能报告，也不能判白方胜利。
            if (room.State != EGameState.Survive)
            {
                return true;
            }

            // 推迟过一次后的重触发：放行原版（否则无限推迟）。
            if (_delayed)
            {
                _delayed = false;
                return false;
            }

            ServerCorpse corpse = room.FindOldestUndiscoveredCorpse();
            if (corpse == null || corpse.DeviceInfo == null || corpse.DeviceInfo.StateList == null)
            {
                return false;       // 没尸体 ⇒ 原版走白方胜利结算
            }

            int reportAt = corpse.DeviceInfo.StateList[5];
            if (reportAt <= 0)
            {
                return false;       // 没排自动报告
            }

            int remain = reportAt - tm.SurviveTime;
            int threshold = ThresholdValue;
            int delay = DelayValue;
            if (remain < threshold || delay <= 0)
            {
                return false;       // 快到了，按原版立即报告
            }


            // ② 推迟后重新触发
            // 推迟期间冻结融毁倒计时：原版 SurvivalTick 见到 Paused 只重排、不 Tick，
            // SurviveTime / RemainTime 都不动 ⇒ 尸体倒计时 job 也一起停。
            _prevPaused = tm.Paused;
            if (!tm.Paused)
            {
                tm.Paused = true;
                _pausedByUs = true;
            }

            _delayed = true;
            room.PushAfter(delay * 1000, Retrigger);

            global::ClueDetail.Core.Log.Info(
                $"任务报告延迟：尸体自动报告剩 {remain} 秒（≥{threshold}），" +
                $"报告与白方结算推迟 {delay} 秒。");

            return true;            // 拦截原版
        }

        /// <summary>把推迟期间按下的暂停恢复成原值（无论走哪条分支都要调）。</summary>
        private static void ResumeTime()
        {
            if (!_pausedByUs)
            {
                return;
            }

            _pausedByUs = false;
            ServerTimeManager tm = ServerTimeManager.Instance;
            if (tm != null)
            {
                tm.Paused = _prevPaused;
            }
        }

        private static void Retrigger()
        {
            ResumeTime();

            try
            {
                ServerGameRoom room = ServerGameRoom.Instance;
                if (room != null && room.State != EGameState.Survive)
                {
                    // 推迟期间已经手动报告 → 走原版的调查流程，这里不再触发任何结算。
                    _delayed = false;
                    return;
                }

                object manager = AccessTools.PropertyGetter(_missionManagerType, "Instance")?.Invoke(null, null);
                _triggerMethod?.Invoke(manager, null);
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "任务报告延迟：重新触发失败。");
            }
        }
    }
}
