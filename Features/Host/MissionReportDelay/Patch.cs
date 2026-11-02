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
    /// <summary>
    /// 白方任务进度满时的「报告 + 结算」延迟（实验性）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 原版链路（0.1.18c）：<c>CheckAllClear</c> → <c>AllClear = true</c> →
    /// <c>TriggerAllClearEnd()</c>：
    /// 有未发现尸体 → <c>DiscoverByTimeOver()</c>（立即报告）；没尸体 →
    /// <c>PushAfter(3000, ClearAllMission)</c>（白方胜利）。
    /// </para>
    /// <para>
    /// 本功能只拦 **任务满** 这一次调用：距尸体自动报告仍 ≥ 阈值时，推迟 N 秒再走原版。
    /// 不碰 <c>TimeManager</c>（融毁倒计时、SurviveTime 一律原版节奏），
    /// 也不碰尸体的 <c>_survivalQueue</c>。
    /// </para>
    /// </remarks>
    internal partial class MissionReportDelayFeature
    {
        private static Type _missionManagerType;
        private static MethodInfo _triggerMethod;

        /// <summary>本次推迟已经发生过一次（防无限推迟）。</summary>
        private static bool _delayed;

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

            // 已经不在生存阶段（手动报告了尸体、或别的原因已进调查/结算）：什么都不做。
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

            _delayed = true;
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
