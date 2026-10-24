﻿﻿﻿﻿using BepInEx.Configuration;
using ClueDetail.Core;

namespace ClueDetail.Features.Host.ShootReminder
{
    /// <summary>
    /// 回放「拍摄」提醒：每次拍摄命中每小时第 2 次时，给黑方 / 黑幕一次全屏变暗。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 需求（2026-10-23）：游戏以 **10 分钟**为间隔采集回放帧，每局有一个 **0–10** 的起始偏移
    /// （= <c>Server.Game.TimelineRecorder.Offset</c>）；「每小时第 2 次拍摄」就是
    /// 帧时间相对偏移 <c>% 60 == 10</c> 的那一帧。
    /// </para>
    /// <para>
    /// ⚠️ **与「时钟破坏」无关**：不读 <c>ShadowSabotage</c> 的任何状态，
    /// 任何生存阶段都按拍摄节拍提醒。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueShootReminder",
        "回放拍摄提醒：每 10 分钟一次拍摄（每局有 0–10 的随机起始偏移），" +
        "命中每小时第 2 次时给黑方 / 黑幕发一次全屏变暗（可配置为所有人）。与时钟破坏无关。",
        defaultEnabled: true,
        side: FeatureSide.Host)]
    internal partial class ShootReminderFeature
    {
        /// <summary>是否发给所有人（默认只发黑方 / 黑幕）。</summary>
        public static ConfigEntry<bool> ToAll { get; private set; }

        /// <summary>提醒特效：0 = 全屏黑 2 秒，1 = 绝望炸弹面板 + 心跳声。</summary>
        public static ConfigEntry<int> EffectChoice { get; private set; }

        /// <summary>true = 每次拍摄都提醒；false = 只在每小时第 2 次提醒。</summary>
        public static ConfigEntry<bool> EveryShot { get; private set; }

        /// <summary>提前多少秒发变暗（游戏秒）。默认 1 ⇒ 2 秒黑屏的中心落在拍摄时刻。</summary>
        public static ConfigEntry<int> LeadSeconds { get; private set; }

        /// <summary>回放记录频率（简化 cron）。见 Bind 里的说明。</summary>
        public static ConfigEntry<string> RecordCron { get; private set; }

        public static void Bind(ConfigFile config, string section)
        {
            ToAll = config.Bind(
                section,
                "ToAll",
                false,
                "变暗提醒是否发给所有存活玩家。\n" +
                "false（默认）= 只发黑方（Dark / Black）与黑幕。");

            EffectChoice = config.Bind(
                section,
                "EffectChoice",
                1,
                "提醒特效：\n" +
                "1（默认）= MineBombVfx（绝望炸弹面板 + 心跳声，时长约 2.85 秒）\n" +
                "0 = BlackOutVfx（全屏黑 2 秒，干净、无残留）");

            EveryShot = config.Bind(
                section,
                "EveryShot",
                false,
                "提醒频率：\n" +
                "false（默认）= 只在每小时第 2 次拍摄提醒（帧时间相对偏移 % 60 == 10）\n" +
                "true = 每次拍摄都提醒（每 10 分钟一次，帧时间相对偏移 % 10 == 0）");

            LeadSeconds = config.Bind(
                section,
                "LeadSeconds",
                1,
                "提前多少秒发提醒（游戏秒，0–5）。默认 1 = MineBombVfx 时长 2.85 秒的一半取整，\n" +
                "让提醒的中点落在拍摄时刻。换 EffectChoice = 0（BlackOut 2 秒）时可改成 1 同样合适。");

            RecordCron = config.Bind(
                section,
                "RecordCron",
                "default",
                "回放记录频率（简化类 cron 语法，时间都带原版 0–10 偏移）：\n" +
                "  default = 原版节拍（每 10 分钟一帧，surviveTime % 10 == Offset）\n" +
                "  */N     = 每 N 分钟一帧（N ≥ 10；10 = 原版密度，再小按 10 处理）\n" +
                "  M */H   = 每 H 小时的 M 分钟那一帧（例：0 */2 = 每 2 小时整点）\n" +
                "  never   = 一次都不记录（极端值，等价于关掉回放采集）");
        }

        internal static bool ToAllValue => ToAll != null && ToAll.Value;

        internal static int EffectChoiceValue => EffectChoice != null ? EffectChoice.Value : 0;

        internal static bool EveryShotValue => EveryShot != null && EveryShot.Value;

        internal static int LeadSecondsValue => LeadSeconds != null ? LeadSeconds.Value : 1;

        internal static string RecordCronValue => RecordCron != null ? RecordCron.Value : "default";
    }
}
