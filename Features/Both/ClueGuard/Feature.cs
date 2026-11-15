using BepInEx.Configuration;
using ClueDetail.Core;

namespace ClueDetail.Features.Both.ClueGuard
{
    /// <summary>
    /// 实验性：销毁线索的二次过滤。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 本段是**无奈之举的补丁修复**：找不到原版这部分代码到底哪里出了问题，
    /// 只能在整批下发 / 客户端列表两处做二次兜底。
    /// </para>
    /// <para>
    /// 两个开关互相独立，默认全开（关掉对应项 = 恢复该项的原版行为）：
    /// </para>
    /// <list type="number">
    /// <item><b>FilterHostBatch</b>：房主侧，调查开始整批下发（<c>S_ADD_MY_CLUES</c>）时剔除
    /// <c>Destroyed</c> 线索。⚠️ 这是**服务端行为改动**，未装本模块的客户端也受影响。</item>
    /// <item><b>FilterClientList</b>：客户端侧，线索进平板列表前拒收 <c>Destroyed</c>。
    /// 只影响装了本模块的客户端。</item>
    /// </list>
    /// <para>
    /// 不做身份二次抹除：客户端拿不到字段 103（真身份），而 101 只是**时间**污染的显示标记，
    /// 用它触发匿名会在时钟破坏时误伤人名（2026-11-15 用户指出，已删）。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueGuard",
        "实验性：销毁线索的二次过滤。⚠️ 无奈之举的补丁修复——找不到原版这部分代码是哪里出了问题，只能在整批 / 列表两处二次兜底（默认全开，可逐项关闭）。",
        defaultEnabled: true,
        side: FeatureSide.Both)]
    internal partial class ClueGuardFeature
    {
        /// <summary>房主侧：整批下发时剔除 Destroyed 线索。</summary>
        public static ConfigEntry<bool> FilterHostBatch { get; private set; }

        /// <summary>客户端侧：平板列表拒收 Destroyed 线索。</summary>
        public static ConfigEntry<bool> FilterClientList { get; private set; }

        public static void Bind(ConfigFile config, string section)
        {
            FilterHostBatch = config.Bind(
                section,
                "FilterHostBatch",
                true,
                "房主侧：调查开始整批下发（S_ADD_MY_CLUES）时剔除 Destroyed 的线索。\n" +
                "⚠️ 无奈之举的补丁修复（找不到原版这部分代码的问题点）。\n" +
                "⚠️ 这是服务端行为改动：未装本模块的客户端也会收到过滤后的整批。");

            FilterClientList = config.Bind(
                section,
                "FilterClientList",
                true,
                "客户端侧：线索进入平板列表前拒收 Destroyed 的线索。\n" +
                "⚠️ 无奈之举的补丁修复（找不到原版这部分代码的问题点）。\n" +
                "只影响装了本模块的客户端；服务端下发内容不变。");
        }
    }
}
