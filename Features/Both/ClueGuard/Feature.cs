using BepInEx.Configuration;
using ClueDetail.Core;

namespace ClueDetail.Features.Both.ClueGuard
{
    /// <summary>
    /// 实验性：销毁 / 身份污染的二次过滤。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 本段是**无奈之举的补丁修复**：找不到原版这部分代码到底哪里出了问题，
    /// 只能在整批下发 / 客户端列表 / 渲染三处做二次兜底。
    /// </para>
    /// <para>
    /// 三个开关互相独立，默认全开（关掉对应项 = 恢复该项的原版行为）：
    /// </para>
    /// <list type="number">
    /// <item><b>FilterHostBatch</b>：房主侧，调查开始整批下发（<c>S_ADD_MY_CLUES</c>）时剔除
    /// <c>Destroyed</c> 线索。⚠️ 这是**服务端行为改动**，未装本模块的客户端也受影响。</item>
    /// <item><b>FilterClientList</b>：客户端侧，线索进平板列表前拒收 <c>Destroyed</c>。
    /// 只影响装了本模块的客户端。</item>
    /// <item><b>RecheckPollution</b>：客户端侧，线索带时间/身份污染标记（字段 101）且记录者不是
    /// 本机时，把句子里的人名二次抹成「某人」——防服务端出站改写被绕过。</item>
    /// </list>
    /// </remarks>
    [PatchFeature(
        "ClueGuard",
        "实验性：销毁与身份污染的二次过滤。⚠️ 无奈之举的补丁修复——找不到原版这部分代码是哪里出了问题，只能在整批 / 列表 / 渲染三处二次兜底（默认全开，可逐项关闭）。",
        defaultEnabled: true,
        side: FeatureSide.Both)]
    internal partial class ClueGuardFeature
    {
        /// <summary>房主侧：整批下发时剔除 Destroyed 线索。</summary>
        public static ConfigEntry<bool> FilterHostBatch { get; private set; }

        /// <summary>客户端侧：平板列表拒收 Destroyed 线索。</summary>
        public static ConfigEntry<bool> FilterClientList { get; private set; }

        /// <summary>客户端侧：污染标记存在时二次抹去人名。</summary>
        public static ConfigEntry<bool> RecheckPollution { get; private set; }

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

            RecheckPollution = config.Bind(
                section,
                "RecheckPollution",
                true,
                "客户端侧：线索带时间/身份污染标记（未知字段 101）且记录者不是本机时，\n" +
                "把渲染句子里的人名二次抹成「某人」。\n" +
                "⚠️ 无奈之举的补丁修复（找不到原版这部分代码的问题点）。\n" +
                "用于兜底「服务端出站改写漏掉」的情况；当事人（本机记录的线索）不受影响。");
        }
    }
}
