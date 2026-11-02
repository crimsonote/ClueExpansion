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
                    return ShouldDelay();
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "任务报告延迟：判断失败（放行原版）。");
                    return true;
                }
            }
        }

        /// <returns>true = 已推迟（拦截原版），false = 放行原版。</returns>
        private static bool ShouldDelay()
        {
            ServerGameRoom room = ServerGameRoom.Instance;
            ServerTimeManager tm = ServerTimeManager.Instance;
            if (room == null || tm == null)
            {
                return false;
            }

            ServerCorpse corpse = room.FindOldestUndiscoveredCorpse();
            if (corpse == null || corpse.DeviceInfo == null || corpse.DeviceInfo.StateList == null)
            {
                return false;       // 没尸体 ⇒ 原版直接走白方胜利结算
            }

            int reportAt = corpse.DeviceInfo.StateList[5];
            if (reportAt <= 0)
            {
                return false;       // 没排自动报告 ⇒ 不介入
            }

            int remain = reportAt - tm.SurviveTime;
            int threshold = ThresholdValue;
            int delay = DelayValue;
            if (remain < threshold || delay <= 0)
            {
                return false;       // 快到了，按原版立即报告
            }

            // ① 倒计时停滞：报告时刻整体后移
            corpse.DeviceInfo.StateList[5] = reportAt + delay;

            // ② 推迟 5 秒后重新触发（期间不报告、不结算）
            room.PushAfter(delay * 1000, Retrigger);

            global::ClueDetail.Core.Log.Info(
                $"任务报告延迟：尸体自动报告剩 {remain} 秒（≥{threshold}），" +
                $"报告与白方结算推迟 {delay} 秒。");

            return true;            // 拦截原版
        }

        private static void Retrigger()
        {
            try
            {
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
