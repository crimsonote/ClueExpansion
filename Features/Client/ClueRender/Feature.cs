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
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueRenderDetail",
        "客户端侧：把详细活动拼回线索句式（旧版「…于{地点}{活动}。」），并在语言表缺少词条时注入旧版 46 条活动词条；本机扫描时记录线索与设备的关联，供房主未装本模块时兜底。",
        defaultEnabled: true,
        side: FeatureSide.Client)]
    internal static partial class ClueRenderFeature
    {
    }
}
