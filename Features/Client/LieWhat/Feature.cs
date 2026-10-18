using ClueDetail.Core;

namespace ClueDetail.Features.Client.LieWhat
{
    /// <summary>
    /// 伪证界面：还原旧版的「详细活动」下拉。
    /// </summary>
    /// <remarks>
    /// 0.1.18 把伪证页的活动下拉整个删了（只剩 who / hour / minute 三个），
    /// 因为协议字段 <c>PropositionInfo.WhatType</c> 也没了。本功能把它补回来：
    /// <list type="bullet">
    /// <item>克隆界面上已有的一个 <c>TMP_Dropdown</c> 作为控件；</item>
    /// <item>选项来源用 <see cref="RoomDeviceScan.CandidatesOf"/>（旧版是
    /// <c>ClueManager.RoomObjectDict</c>，0.1.18 已删除）—— 即"这个房间里有哪些活动"；</item>
    /// <item>选中后写进 <c>LieClue.Clue.Info</c> 的 protobuf 未知字段（号 4）。</item>
    /// </list>
    /// <para>
    /// 提交链路不需要任何改动：客户端 <c>OnClickPropositionButton</c> 会
    /// <c>Clue.Info.Clone()</c> 后整包发出，服务端 <c>SubmitProposition</c> 又是**原样**保存并广播
    /// （0.1.18 :182232-182246），所以未知字段会一路传到所有客户端。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueLieWhatDropdown",
        "伪证界面还原旧版的「详细活动」下拉：选项取该房间已有的活动，选中后随伪证一起下发（未装本模块的客户端忽略该字段，行为与原版一致）。",
        defaultEnabled: true,
        side: FeatureSide.Client)]
    internal partial class LieWhatFeature
    {
        /// <summary>
        /// 热关闭：撤掉伪证页里那个下拉。
        /// </summary>
        /// <remarks>热开启时不需要额外动作 —— 打开伪证页时原版会走 <c>Init</c>，克隆会重新发生。</remarks>
        public static void OnDisabled()
        {
            LieWhatDropdown.Destroy();
        }
    }
}
