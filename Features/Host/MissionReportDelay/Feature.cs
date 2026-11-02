using System;
using System.Reflection;
using BepInEx.Configuration;
using ClueDetail.Core;
using HarmonyLib;
using ServerGameRoom = Server.Game.GameRoom;
using ServerTimeManager = Server.Game.TimeManager;

namespace ClueDetail.Features.Host.MissionReportDelay
{
    /// <summary>
    /// 白方任务进度满时的「报告 + 结算」延迟（实验性）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 原版链路（0.1.18c）：
    /// <code>
    /// MissionManager.CheckAllClear → AllClear = true → TriggerAllClearEnd()
    ///   ├ 有未发现尸体 → corpse.DiscoverByTimeOver()   （立即自动报告，模糊死亡时间）
    ///   └ 没尸体       → PushAfter(3000, ClearAllMission)（白方胜利结算）
    /// </code>
    /// 于是有人会在白任务快满时刀人，借「任务满」把尸体立刻推成已报告。
    /// </para>
    /// <para>
    /// 本功能：当「距尸体自动报告倒计时」≥ <see cref="ThresholdSeconds"/> 时，
    /// 把这次报告 + 结算整体推迟 <see cref="DelaySeconds"/> 秒；推迟期间：
    /// ① 不触发 <c>DiscoverByTimeOver</c>（不报告）；
    /// ② 不触发 <c>ClearAllMission</c>（白方不结算）；
    /// ③ 尸体的自动报告时刻 <c>StateList[5]</c> 同步 +delay（倒计时停滞）。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueMissionReportDelay",
        "白方任务满时，若距尸体自动报告还有 ≥阈值秒，则把报告与白方结算推迟 N 秒（倒计时同步停滞）。",
        defaultEnabled: false,
        side: FeatureSide.Host)]
    internal partial class MissionReportDelayFeature
    {
        /// <summary>推迟秒数。</summary>
        public static ConfigEntry<int> DelaySeconds { get; private set; }

        /// <summary>只有剩余倒计时 ≥ 该值才推迟（秒）。</summary>
        public static ConfigEntry<int> ThresholdSeconds { get; private set; }

        public static void Bind(ConfigFile config, string section)
        {
            DelaySeconds = config.Bind(
                section,
                "DelaySeconds",
                5,
                "任务满时的报告 + 结算推迟多少秒（1–30）。");

            ThresholdSeconds = config.Bind(
                section,
                "ThresholdSeconds",
                40,
                "尸体自动报告倒计时剩余 ≥ 该值（秒）才推迟；小于它按原版立即报告。");
        }

        internal static int DelayValue => DelaySeconds != null ? DelaySeconds.Value : 5;

        internal static int ThresholdValue => ThresholdSeconds != null ? ThresholdSeconds.Value : 40;
    }
}
