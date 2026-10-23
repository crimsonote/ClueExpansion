using BepInEx.Configuration;
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
                0,
                "提醒特效：\n" +
                "0（默认）= BlackOutVfx（全屏黑 2 秒，干净、无残留）\n" +
                "1 = MineBombVfx（绝望炸弹面板 + 心跳声，可能残留到大厅）");
        }

        internal static bool ToAllValue => ToAll != null && ToAll.Value;

        internal static int EffectChoiceValue => EffectChoice != null ? EffectChoice.Value : 0;
    }
}
