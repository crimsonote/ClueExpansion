using System;
using System.Reflection;
using ClueDetail.Core;
using HarmonyLib;
using Protocol;
using ServerCorpse = Server.Game.Corpse;
using ServerGameRoom = Server.Game.GameRoom;
using ServerJobElem = Server.Game.JobElem;
using ServerTimeManager = Server.Game.TimeManager;

namespace ClueDetail.Features.Host.MissionReportDelay
{
    /// <summary>
    /// 任务满延迟的全部运行期逻辑（补丁类只做入口检查与转发）。
    /// </summary>
    /// <remarks>
    /// 状态机：
    /// <code>
    /// 正常
    ///   └ TriggerAllClearEnd
    ///       ├ 无尸体 / 倒计时不够 / 延迟配置为 0 → 放行原版
    ///       └ 有尸体且剩余 ≥ 阈值 → _pending=true，冻结 RemainTime，PushAfter(D, Retrigger)
    /// 推迟中
    ///   └ Retrigger → 解冻 → 再走一次原版 TriggerAllClearEnd
    /// </code>
    /// 任何阶段切换 / 换局 / 手动报告都会 <see cref="Reset"/>；关功能时若仍在推迟中，
    /// 会立即补走原版（否则 AllClear 会让 <c>SurvivalTick</c> 停摆，整局卡死）。
    /// </remarks>
    internal partial class MissionReportDelayFeature
    {
        /// <summary>MissionManager 是 internal，只能按名字找。</summary>
        private static Type _missionType;

        private static MethodInfo _triggerMethod;
        private static PropertyInfo _instanceProp;
        private static PropertyInfo _allClearProp;

        /// <summary>TimeManager.RemainTime 的私有 setter；缺失时只放弃冻结，不影响延迟本身。</summary>
        private static MethodInfo _remainSetter;

        private static bool _broken;
        private static bool _loggedBroken;
        private static bool _freezeBroken;
        private static bool _loggedFreezeBroken;

        /// <summary>本次推迟进行中。期间所有 TriggerAllClearEnd 都被拦下。</summary>
        private static bool _pending;

        /// <summary>Retrigger 调用原版时放行一次。</summary>
        private static bool _bypass;

        /// <summary>熔毁倒计时冻结。</summary>
        private static bool _freeze;
        private static float _frozenRemain;
        private static bool _freezeCorrected;

        /// <summary>推迟到点的 job；关功能 / 换局时 Kill。</summary>
        private static ServerJobElem _job;

        // ══════════════════ 反射 ══════════════════

        private static void ResolveReflection()
        {
            if (_broken && _missionType == null)
            {
                return;
            }

            if (_missionType == null)
            {
                _missionType = AccessTools.TypeByName("Server.Game.MissionManager");
            }

            if (_missionType == null)
            {
                MarkBroken();
                return;
            }

            if (_triggerMethod == null)
            {
                _triggerMethod = AccessTools.Method(_missionType, "TriggerAllClearEnd");
            }

            if (_instanceProp == null)
            {
                _instanceProp = AccessTools.Property(_missionType, "Instance");
            }

            if (_allClearProp == null)
            {
                _allClearProp = AccessTools.Property(_missionType, "AllClear");
            }

            if (_remainSetter == null)
            {
                PropertyInfo remain = AccessTools.Property(typeof(ServerTimeManager), "RemainTime");
                _remainSetter = remain?.GetSetMethod(true);
                if (_remainSetter == null)
                {
                    _freezeBroken = true;
                    if (!_loggedFreezeBroken)
                    {
                        _loggedFreezeBroken = true;
                        global::ClueDetail.Core.Log.Warn(
                            "任务报告延迟：找不到 TimeManager.RemainTime 的 setter，倒计时冻结不可用（延迟本身照常）。");
                    }
                }
            }

            if (_triggerMethod == null || _instanceProp == null || _allClearProp == null)
            {
                MarkBroken();
            }
        }

        private static void MarkBroken()
        {
            _broken = true;
            if (_loggedBroken)
            {
                return;
            }

            _loggedBroken = true;
            global::ClueDetail.Core.Log.Error(
                "任务报告延迟：找不到 MissionManager.TriggerAllClearEnd / Instance / AllClear，功能退回原版。");
        }

        private static bool ReflectionReady()
        {
            return !_broken && _triggerMethod != null && _instanceProp != null && _allClearProp != null;
        }

        // ══════════════════ 决策 ══════════════════

        /// <returns>true = 放行原版；false = 拦下原版（本次什么都不做）。</returns>
        internal static bool ShouldRunOriginal()
        {
            ResolveReflection();
            if (!ReflectionReady())
            {
                return true;
            }

            if (_bypass)
            {
                _bypass = false;
                return true;
            }

            ServerGameRoom room = ServerGameRoom.Instance;
            ServerTimeManager tm = ServerTimeManager.Instance;
            if (room == null || tm == null)
            {
                return true;
            }

            // 已经不在生存阶段（手动报告等）：绝不能放原版去排 ClearAllMission。
            if (room.State != EGameState.Survive)
            {
                Reset(false);
                return false;
            }

            if (_pending)
            {
                return false;
            }

            if (!IsAllClear())
            {
                return true;
            }

            ServerCorpse corpse = room.FindOldestUndiscoveredCorpse();
            if (corpse == null || corpse.DeviceInfo == null || corpse.DeviceInfo.StateList == null
                || corpse.DeviceInfo.StateList.Count <= 5)
            {
                return true; // 无未发现尸体 → 原版白方胜利
            }

            int reportAt = corpse.DeviceInfo.StateList[5];
            if (reportAt <= 0)
            {
                return true; // 没有排自动报告
            }

            int remain = reportAt - tm.SurviveTime;
            int threshold = ThresholdValue;
            int delay = DelayValue;
            if (delay <= 0 || remain < threshold)
            {
                return true; // 快到了，按原版立即报告
            }

            StartDelay(room, tm, delay, remain, threshold);
            return false;
        }

        private static void StartDelay(
            ServerGameRoom room, ServerTimeManager tm, int delay, int remain, int threshold)
        {
            _frozenRemain = tm.RemainTime;
            _freeze = !_freezeBroken;
            _freezeCorrected = false;
            _pending = true;

            try
            {
                _job?.Kill();
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "任务报告延迟：取消旧 job 失败。");
            }

            _job = room.PushAfter(delay * 1000, Retrigger);
            global::ClueDetail.Core.Log.Info(
                $"任务报告延迟：白方任务满；最老尸体自动报告剩 {remain} 秒（≥{threshold}），" +
                $"报告与白方结算推迟 {delay} 秒，倒计时冻结在 {_frozenRemain:F1}" +
                (_freeze ? "。" : "（冻结不可用）。"));
        }

        // ══════════════════ 恢复 ══════════════════

        internal static void Retrigger()
        {
            try
            {
                RetriggerCore();
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "任务报告延迟：恢复原版异常，立即补走原版。");
                Reset(true);
            }
        }

        private static void RetriggerCore()
        {
            _job = null;
            ServerGameRoom room = ServerGameRoom.Instance;

            if (!FeatureLoader.Enabled<MissionReportDelayFeature>())
            {
                // 关功能时如果 job 先到，也要把原版补走，绝不能只清状态把 AllClear 晾着。
                Reset(true);
                return;
            }

            if (room == null)
            {
                Reset(false);
                return;
            }

            ClearFreeze();

            if (room.State != EGameState.Survive)
            {
                global::ClueDetail.Core.Log.Info(
                    "任务报告延迟：推迟期间已进入调查（手动报告），不再补发报告。");
                Reset(false);
                return;
            }

            if (!IsAllClear())
            {
                global::ClueDetail.Core.Log.Warn("任务报告延迟：到点时已不在任务满状态，放弃补发。");
                Reset(false);
                return;
            }

            global::ClueDetail.Core.Log.Info("任务报告延迟：推迟到点，恢复原版流程（报告尸体 / 白方结算）。");
            _pending = true;
            InvokeOriginal(room);
            _pending = false;
        }

        private static void InvokeOriginal(ServerGameRoom room)
        {
            object manager = null;
            try
            {
                manager = _instanceProp?.GetValue(null);
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "任务报告延迟：读取 MissionManager.Instance 失败。");
            }

            if (manager == null || _triggerMethod == null)
            {
                global::ClueDetail.Core.Log.Error("任务报告延迟：反射目标不可用，改为直接报告最老尸体。");
                FallbackDiscover(room);
                return;
            }

            _bypass = true;
            try
            {
                _triggerMethod.Invoke(manager, null);
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "任务报告延迟：重新触发原版失败。");
                _bypass = false;
                FallbackDiscover(room);
            }
            finally
            {
                _bypass = false;
            }
        }

        private static void FallbackDiscover(ServerGameRoom room)
        {
            try
            {
                room?.FindOldestUndiscoveredCorpse()?.DiscoverByTimeOver();
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "任务报告延迟：兜底报告尸体失败。");
            }
        }

        // ══════════════════ 冻结 ══════════════════

        /// <summary>把 RemainTime 压回冻结值，并广播纠正（只影响倒计时，不动 SurviveTime）。</summary>
        internal static void ApplyFreeze(ServerTimeManager tm)
        {
            if (!_freeze || tm == null)
            {
                return;
            }

            if (_remainSetter == null)
            {
                _freeze = false;
                return;
            }

            float frozen = _frozenRemain;
            if (Math.Abs(tm.RemainTime - frozen) <= 0.01f)
            {
                return;
            }

            float before = tm.RemainTime;
            try
            {
                _remainSetter.Invoke(tm, new object[] { frozen });
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "任务报告延迟：写回冻结倒计时失败，停止冻结。");
                _freeze = false;
                return;
            }

            ServerGameRoom room = ServerGameRoom.Instance;
            if (room != null)
            {
                try
                {
                    room.Broadcast(new S_UPDATE_REMAIN_TIME
                    {
                        RemainTime = (int)frozen,
                    });
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "任务报告延迟：广播冻结倒计时失败。");
                }
            }

            if (!_freezeCorrected)
            {
                _freezeCorrected = true;
                global::ClueDetail.Core.Log.Info(
                    $"任务报告延迟：推迟期间倒计时被改动（{before:F1} → 已回滚到 {frozen:F1}）。");
            }
        }

        private static void ClearFreeze()
        {
            _freeze = false;
            _freezeCorrected = false;
        }

        // ══════════════════ 生命周期 ══════════════════

        /// <summary>任何阶段切换 / 换局 / 关功能都清一次。</summary>
        internal static void Reset(bool resumeOriginal)
        {
            bool wasPending = _pending;
            _pending = false;
            _bypass = false;
            ClearFreeze();

            try
            {
                _job?.Kill();
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "任务报告延迟：取消推迟 job 失败。");
            }

            _job = null;

            if (!resumeOriginal || !wasPending)
            {
                return;
            }

            ServerGameRoom room = ServerGameRoom.Instance;
            if (room == null || room.State != EGameState.Survive)
            {
                return;
            }

            if (!IsAllClear())
            {
                global::ClueDetail.Core.Log.Info("任务报告延迟：功能关闭时已不在任务满状态，不再补发。");
                return;
            }

            global::ClueDetail.Core.Log.Info("任务报告延迟：功能被关闭，立即按原版补发报告 / 结算。");
            InvokeOriginal(room);
        }

        /// <summary>阶段切换（<c>GameRoom.ChangeGameState</c>）后清状态。</summary>
        internal static void OnStateChanged()
        {
            Reset(false);
        }

        private static bool IsAllClear()
        {
            if (_instanceProp == null || _allClearProp == null)
            {
                return false;
            }

            try
            {
                object manager = _instanceProp.GetValue(null);
                if (manager == null)
                {
                    return false;
                }

                object value = _allClearProp.GetValue(manager);
                return value is bool flag && flag;
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "任务报告延迟：读取 AllClear 失败。");
                return false;
            }
        }
    }
}
