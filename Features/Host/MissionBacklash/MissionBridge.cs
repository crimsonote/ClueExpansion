using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace ClueDetail.Features.Host.MissionBacklash
{
    /// <summary>
    /// <c>Server.Game.MissionManager</c> 的反射访问器（发行程序集里是 <c>internal class</c>，
    /// 编译期不可见，只能反射）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 反射目标（0.1.18，升级时全文搜索 <c>MissionManager</c> 复核）：
    /// <list type="bullet">
    /// <item><c>Type</c> 全名 <c>Server.Game.MissionManager</c> → :169148（internal class）</item>
    /// <item><c>Instance</c>（public static）→ :169152</item>
    /// <item><c>GoalPoint</c>（public get / private set）→ :169154</item>
    /// <item><c>CurrentPoint</c>（public get / private set）→ :169156</item>
    /// <item><c>EscapeGaugeWeight</c> → :169158</item>
    /// <item><c>TimeLimitIncreaseWeight</c> → :169160</item>
    /// <item><c>PublicRemainPlayerCount</c> → :169162</item>
    /// <item><c>ProgressMissionList</c>（public List&lt;MissionData&gt;）→ :169164</item>
    /// <item><c>AllClear</c> → :169168</item>
    /// <item><c>ClearMission(ESchoolMission, Player, bool)</c>（public）→ :169575</item>
    /// </list>
    /// </para>
    /// <para>
    /// 定位方式：<c>AccessTools.TypeByName</c> 按全名查，不硬编码程序集名。
    /// 失败一律返回 null / false，由调用方按上下文决定是否记日志 —— 反射断了不该让游戏崩。
    /// </para>
    /// </remarks>
    internal static class MissionBridge
    {
        private const string TypeName = "Server.Game.MissionManager";

        private static bool _init;
        private static Type _type;
        private static PropertyInfo _instance;
        private static PropertyInfo _progressList;
        private static PropertyInfo _goalPoint;
        private static PropertyInfo _currentPoint;
        private static PropertyInfo _escapeWeight;
        private static PropertyInfo _timeWeight;
        private static PropertyInfo _remainPlayers;
        private static PropertyInfo _allClear;

        /// <summary>是否成功拿到类型与全部成员（拿不到时功能整体空转）。</summary>
        internal static bool Ready
        {
            get
            {
                EnsureInit();
                return _type != null && _instance != null && _currentPoint != null;
            }
        }

        /// <summary>服务端 <c>MissionManager.Instance</c>（拿不到就是 null）。</summary>
        internal static object Instance
        {
            get
            {
                EnsureInit();
                if (_instance == null)
                {
                    return null;
                }

                try
                {
                    return _instance.GetValue(null);
                }
                catch (Exception ex)
                {
                    global::ClueDetail.Core.Log.Exception(ex, "任务倒扣：读取 MissionManager.Instance 失败。");
                    return null;
                }
            }
        }

        /// <summary>进行中的任务列表（<c>List&lt;Data.MissionData&gt;</c>），以 <see cref="IList"/> 暴露。</summary>
        internal static IList ProgressList(object manager)
        {
            EnsureInit();
            if (manager == null || _progressList == null)
            {
                return null;
            }

            try
            {
                return _progressList.GetValue(manager, null) as IList;
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "任务倒扣：读取 ProgressMissionList 失败。");
                return null;
            }
        }

        /// <summary>列表里是否还有该任务类型（判断 <c>ClearMission</c> 是否真的清掉了它）。</summary>
        internal static bool HasMission(object manager, int missionType)
        {
            IList list = ProgressList(manager);
            if (list == null)
            {
                return false;
            }

            for (int i = 0; i < list.Count; i++)
            {
                var data = list[i] as Data.MissionData;
                if (data != null && data.Type == missionType)
                {
                    return true;
                }
            }

            return false;
        }

        internal static float GoalPoint(object manager)
        {
            return ReadFloat(manager, _goalPoint);
        }

        internal static float CurrentPoint(object manager)
        {
            return ReadFloat(manager, _currentPoint);
        }

        /// <summary>写 <c>CurrentPoint</c>（private setter，走 <c>GetSetMethod(true)</c>）。</summary>
        internal static bool SetCurrentPoint(object manager, float value)
        {
            EnsureInit();
            if (manager == null || _currentPoint == null)
            {
                return false;
            }

            try
            {
                MethodInfo setter = _currentPoint.GetSetMethod(true);
                if (setter == null)
                {
                    return false;
                }

                setter.Invoke(manager, new object[] { value });
                return true;
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "任务倒扣：写入 CurrentPoint 失败。");
                return false;
            }
        }

        internal static float EscapeGaugeWeight(object manager)
        {
            return ReadFloat(manager, _escapeWeight);
        }

        internal static float TimeLimitIncreaseWeight(object manager)
        {
            return ReadFloat(manager, _timeWeight);
        }

        /// <summary>当前存活人数（原版用它算每任务回血：<c>15 / PublicRemainPlayerCount</c>，整数除法）。</summary>
        internal static int PublicRemainPlayerCount(object manager)
        {
            EnsureInit();
            if (manager == null || _remainPlayers == null)
            {
                return 1;
            }

            try
            {
                object value = _remainPlayers.GetValue(manager, null);
                return value is int n && n > 0 ? n : 1;
            }
            catch
            {
                return 1;
            }
        }

        /// <summary><c>AllClear</c> 一旦为真，生存阶段即将结束（原版会锁死设备交互）。</summary>
        internal static bool AllClear(object manager)
        {
            EnsureInit();
            if (manager == null || _allClear == null)
            {
                return false;
            }

            try
            {
                return _allClear.GetValue(manager, null) is bool b && b;
            }
            catch
            {
                return false;
            }
        }

        private static float ReadFloat(object manager, PropertyInfo property)
        {
            EnsureInit();
            if (manager == null || property == null)
            {
                return 0f;
            }

            try
            {
                object value = property.GetValue(manager, null);
                return value is float f ? f : 0f;
            }
            catch
            {
                return 0f;
            }
        }

        private static void EnsureInit()
        {
            if (_init)
            {
                return;
            }

            _init = true;
            try
            {
                _type = AccessTools.TypeByName(TypeName);
                if (_type == null)
                {
                    global::ClueDetail.Core.Log.Warn(
                        $"任务倒扣：找不到类型 {TypeName}，本功能将空转（游戏版本可能已变）。");
                    return;
                }

                _instance = _type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                _progressList = _type.GetProperty("ProgressMissionList", BindingFlags.Public | BindingFlags.Instance);
                _goalPoint = _type.GetProperty("GoalPoint", BindingFlags.Public | BindingFlags.Instance);
                _currentPoint = _type.GetProperty("CurrentPoint", BindingFlags.Public | BindingFlags.Instance);
                _escapeWeight = _type.GetProperty("EscapeGaugeWeight", BindingFlags.Public | BindingFlags.Instance);
                _timeWeight = _type.GetProperty("TimeLimitIncreaseWeight", BindingFlags.Public | BindingFlags.Instance);
                _remainPlayers = _type.GetProperty("PublicRemainPlayerCount", BindingFlags.Public | BindingFlags.Instance);
                _allClear = _type.GetProperty("AllClear", BindingFlags.Public | BindingFlags.Instance);

                if (_instance == null || _currentPoint == null || _progressList == null)
                {
                    global::ClueDetail.Core.Log.Warn(
                        "任务倒扣：MissionManager 的成员形状与 0.1.18 不符，本功能将空转。");
                }
            }
            catch (Exception ex)
            {
                global::ClueDetail.Core.Log.Exception(ex, "任务倒扣：反射初始化失败。");
            }
        }
    }
}
