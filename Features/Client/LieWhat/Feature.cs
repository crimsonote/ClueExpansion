using BepInEx.Configuration;
using ClueDetail.Core;

namespace ClueDetail.Features.Client.LieWhat
{
    /// <summary>
    /// 伪证界面：还原旧版的「详细活动」那一行。
    /// </summary>
    /// <remarks>
    /// 0.1.18 把这一行整行删了（枚举里的 <c>WhatDrop</c>/<c>LieWhatText</c>、代码里的
    /// <c>RefreshWhat</c>/<c>OnWhatDropdownValueChanged</c> 全没了），因为协议字段
    /// <c>PropositionInfo.WhatType</c> 也没了。本功能把它补回来：
    /// <list type="bullet">
    /// <item><b>整行</b>：克隆 <c>LieList</c> 下结构相同的 <c>Who</c> 行（行标题 + 一个下拉），
    /// 改名后按旧版位置放回 <c>When</c> 行下方 —— 旧版 <c>WhatDrop</c> 就是 550x60（见 Logic.cs 注释）；</item>
    /// <item><b>行标题</b>：0.1.18 连 <c>What</c> 词条一起删了，所以走本模块自有词条
    /// （<c>Localization.WhatLabelKey</c>，5 语言）；</item>
    /// <item><b>选项</b>：用 <see cref="RoomDeviceScan.CandidatesOf"/>（旧版是已删除的
    /// <c>ClueManager.RoomObjectDict</c>）取「这个房间里有哪些活动」，**只列真有的**；
    /// 房间一台设备都没有时默认只留一个占位项（要列全部活动得显式开
    /// <see cref="AllowAnyActivity"/>）；</item>
    /// <item><b>写入</b>：选中后写进 <c>LieClue.Clue.Info</c> 的 protobuf 未知字段（号 100）。</item>
    /// </list>
    /// <para>
    /// 提交链路不需要任何改动：客户端 <c>OnClickPropositionButton</c> 会
    /// <c>Clue.Info.Clone()</c> 后整包发出，服务端 <c>SubmitProposition</c> 又是**原样**保存并广播
    /// （0.1.18 :182232-182246），所以未知字段会一路传到所有客户端。
    /// </para>
    /// <para>
    /// ⚠️ 与 0.1.17a 的一处**刻意的**差别：「能不能在这个房间伪造」仍按 0.1.18 的服务端口径
    /// （<c>CanLieInRoom</c> :53713 判 <c>ClueManager.LieRoomSet</c>），没有恢复旧版的
    /// 「房间必须有设备」——房间准入是服务端的规则，本模块不替它收紧；我们只约束**列表里出现什么**。
    /// </para>
    /// </remarks>
    [PatchFeature(
        "ClueLieWhatDropdown",
        "伪证界面还原旧版的「活动」一行（行标题 + 下拉）：选项**只列该房间里真实存在的设备活动**，选中后随伪证一起下发；未装本模块的客户端忽略该字段，行为与原版一致。",
        defaultEnabled: true,
        side: FeatureSide.Client)]
    internal partial class LieWhatFeature
    {
        /// <summary>
        /// 房间没有任何可调查设备时，是否允许把**全部**活动列进下拉。
        /// </summary>
        /// <remarks>
        /// 默认关：伪证要"说得通"——让人在图书馆伪造「打开了黑洞」没有意义。
        /// 0.1.17a 更狠：那种房间根本点不进来（<c>HasLieWhatOptions</c> :53482 判
        /// <c>ClueManager.RoomObjectDict</c>）；0.1.18 改成只看服务端下发的 <c>LieRoomSet</c>
        /// （<c>CanLieInRoom</c> :53713），于是没有设备的房间也能进来伪造。
        /// </remarks>
        public static ConfigEntry<bool> AllowAnyActivity { get; private set; }

        public static void Bind(ConfigFile config, string section)
        {
            AllowAnyActivity = config.Bind(
                section,
                "AllowAnyActivity",
                false,
                "房间里没有任何可调查设备时，是否允许在「活动」下拉里列出**全部**活动。\n" +
                "默认关：只列这个房间真有的设备（例：图书馆里不会出现「打开了黑洞」）。\n" +
                "打开它 = 允许在没有设备的房间伪造任意活动（玩法取向，不是修 bug）。");
        }

        /// <summary>
        /// 热关闭：撤掉伪证页里那一行。
        /// </summary>
        /// <remarks>热开启时不需要额外动作 —— 打开伪证页时原版会走 <c>Init</c>，克隆会重新发生。</remarks>
        public static void OnDisabled()
        {
            LieWhatRow.Destroy();
        }
    }
}
