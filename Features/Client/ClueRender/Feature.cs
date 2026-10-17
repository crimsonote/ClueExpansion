using BepInEx.Configuration;
using ClueDetail.Core;

namespace ClueDetail.Features.Client.ClueRender
{
    /// <summary>
    /// 客户端侧：把「详细活动」拼回线索句式。
    /// </summary>
    /// <remarks>
    /// 0.1.18 三处同时移除了这一段：协议字段（WhatType）、句子模板（<c>PropositionSentence*</c>
    /// 去掉了 <c>{what}</c>）、以及语言表里的 46 条 <c>W*</c> 词条。
    /// 本功能负责后两者在客户端的还原：
    /// <list type="bullet">
    /// <item>语言表：把 46 条词条注入回 <c>Managers.Data.TextDic</c>（只补缺失键，不覆盖原版）；</item>
    /// <item>句子：在 <c>Util.FormatProposition</c> 的结果里把活动插回句末标点之前。</item>
    /// </list>
    /// <para>
    /// 详细活动的来源有两级：优先读服务端经 protobuf 未知字段下发的值；
    /// 房主未装本模块时退化为客户端自采集的「线索 → 设备」关联（<see cref="ActivityMap"/>）。
    /// 房主装了本模块但关闭了下发时，会收到显式标记，此时**连自采集一起停**。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueRenderDetail",
        "客户端侧：把详细活动拼回线索句式（旧版「…于{地点}{活动}。」），并在语言表缺少词条时注入旧版 46 条活动词条；房主未装本模块时，本机扫描会记录线索与设备的关联作为兜底。",
        defaultEnabled: true,
        side: FeatureSide.Client)]
    internal static partial class ClueRenderFeature
    {
        public static void Bind(ConfigFile config, string section)
        {
            SelfCapture.Enabled = config.Bind(
                section,
                "SelfCapture",
                true,
                "房主没有安装本模块时，客户端自行记录「线索 ↔ 设备」的关联，用来反查该线索的详细活动。\n" +
                "房主装了本模块时会由服务端下发，本项不起作用；房主装了但关闭下发时，本项会被强制停用。");
        }
    }
}
