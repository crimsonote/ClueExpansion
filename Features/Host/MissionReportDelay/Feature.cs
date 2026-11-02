using System;
using BepInEx.Configuration;
using ClueDetail.Core;

namespace ClueDetail.Features.Host.MissionReportDelay
{
    /// <summary>
    /// 白方任务进度满时的「报告尸体 / 白方结算」延迟。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 原版链路（0.1.18）：<c>MissionManager.ClearMission</c> → <c>CheckAllClear</c> 置
    /// <c>AllClear = true</c> → <c>TriggerAllClearEnd()</c>（:169787）：
    /// 有未发现尸体 → <c>Corpse.DiscoverByTimeOver()</c>；没尸体 →
    /// <c>PushAfter(3000, ClearAllMission)</c>（白方胜利）。
    /// </para>
    /// <para>
    /// 本功能只拦 <b>任务满</b> 这一次调用：有未发现尸体且它的自动报告倒计时剩余
    /// ≥ <c>ThresholdSeconds</c> 时，整体推迟 <c>DelaySeconds</c> 秒再走原版；
    /// 推迟期间熔毁倒计时冻结（不变大也不变小），给白方多几秒活动时间。
    /// 手动报告 / 倒计时归零报告等其它进入调查的途径完全不受影响。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueMissionReportDelay",
        "白方任务满时：若已有未发现尸体、且它的自动报告倒计时剩余 ≥ 阈值，\n" +
        "把「报告尸体 + 白方结算」整体推迟 N 秒；推迟期间熔毁倒计时冻结（不变大也不变小），\n" +
        "给白方多 N 秒活动时间。5 秒后恢复原版流程。手动报告与其它进入调查的途径不受影响。\n" +
        "阈值与延时可热调。",
        defaultEnabled: false,
        side: FeatureSide.Host)]
    internal partial class MissionReportDelayFeature
    {
        /// <summary>推迟秒数。</summary>
        public static ConfigEntry<int> DelaySeconds { get; private set; }

        /// <summary>只有尸体自动报告倒计时剩余 ≥ 该值才推迟（秒）。</summary>
        public static ConfigEntry<int> ThresholdSeconds { get; private set; }

        public static void Bind(ConfigFile config, string section)
        {
            DelaySeconds = config.Bind(
                section,
                "DelaySeconds",
                5,
                new ConfigDescription(
                    "任务满时的报告 + 结算推迟多少秒。\n" +
                    "推迟期间熔毁倒计时冻结，给白方多这么多秒活动时间。允许 1–30。",
                    new AcceptableValueRange<int>(1, 30)));

            ThresholdSeconds = config.Bind(
                section,
                "ThresholdSeconds",
                40,
                new ConfigDescription(
                    "尸体自动报告倒计时剩余 ≥ 该值（秒）才推迟；小于它按原版立即报告。\n" +
                    "默认 40（尸体自动报告窗口是 50–70 秒）。允许 0–120。",
                    new AcceptableValueRange<int>(0, 120)));
        }

        internal static int DelayValue =>
            DelaySeconds == null ? 5 : Math.Clamp(DelaySeconds.Value, 1, 30);

        internal static int ThresholdValue =>
            ThresholdSeconds == null ? 40 : Math.Clamp(ThresholdSeconds.Value, 0, 120);

        /// <summary>启动时自检反射目标；与开关状态无关。</summary>
        public static void OnLoaded()
        {
            ResolveReflection();
            if (!_broken)
            {
                global::ClueDetail.Core.Log.Info("任务报告延迟：反射目标齐全（TriggerAllClearEnd / AllClear / RemainTime）。");
            }
        }

        /// <summary>热关：若正处在推迟中，立即补走原版，避免 AllClear 停摆导致卡局。</summary>
        public static void OnDisabled()
        {
            Reset(true);
        }
    }
}
