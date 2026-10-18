using BepInEx.Configuration;
using ClueDetail.Core;

namespace ClueDetail.Features.Client.ClueRender
{
    /// <summary>
    /// 客户端侧：把「详细活动」拼回线索句式。
    /// </summary>
    /// <remarks>
    /// 0.1.18 移除了这一段的数据来源：协议字段（<c>PropositionInfo.WhatType</c>）与语言表里的
    /// 46 条 <c>W*</c> 词条。⚠️ **句子模板里的 <c>{what}</c> 并没有删**（2026-10-18 更正：
    /// 早前记成"模板去掉了 {what}"是错的）—— <c>Util.FormatProposition</c>（:27343）的
    /// <c>case "what"</c> 分支仍在，只是内容降级成了角色级两选一
    /// <c>GetRoleWhatKey(playerId)</c>（:27517 / :27379）。所以本功能做的是**替换**那一段，
    /// 而不是往句子里再插一段（见 <see cref="ClueWhat.TryDecorate"/>）。
    /// <para>
    /// 本功能负责后两者在客户端的还原：
    /// <list type="bullet">
    /// <item>语言表：把 46 条词条注入回 <c>Managers.Data.TextDic</c>（只补缺失键，不覆盖原版）；</item>
    /// <item>句子：把 <c>Util.FormatProposition</c> 结果里 FF97AB 色号那一段换成真实活动。</item>
    /// </list>
    /// </para>
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
    internal partial class ClueRenderFeature
    {
        public static void Bind(ConfigFile config, string section)
        {
            SelfCapture.Enabled = config.Bind(
                section,
                "SelfCapture",
                true,
                "房主没有安装本模块时，客户端自行记录「线索 ↔ 设备」的关联，用来反查该线索的详细活动。\n" +
                "房主装了本模块时会由服务端下发，本项不起作用；房主装了但关闭下发时，本项会被强制停用。");

            Diagnostics.LogClueResolution = config.Bind(
                section,
                "LogResolution",
                true,
                "在日志里打印每条线索的详细活动**是从哪一级拿到的**（包内未知字段 / 房主旁路表 /\n" +
                "设备关联 / 房间唯一设备 / 房间有几种设备 / 房间无设备 / 本局禁用），以及房主侧的下发命中情况。\n" +
                "只用于排查「卡片显示的活动不对、悬浮整句里没有活动」这类问题；每条链路最多 40 行。").Value;
        }
    }
}
